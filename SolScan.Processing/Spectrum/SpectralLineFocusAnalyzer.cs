using SolScan.Core.Camera;
using SolScan.Processing.Math;
using SolScan.Processing.Shg;

namespace SolScan.Processing.Spectrum;

/// <summary>
/// One frame's camera-focus reading from <see cref="SpectralLineFocusAnalyzer.Measure(CameraFrame, QuadraticPolynomial?)"/>.
/// <see cref="FwhmPixels"/> is the number to *minimize* while dialing in the camera's own optical focus:
/// the full width, at half the line's depth, of the studied spectral line along the dispersion axis.
/// <see cref="DepthFraction"/> is how deep that line's dip is relative to its local continuum (0-1) -
/// shown for context, since a very shallow dip makes the width less trustworthy.
/// <see cref="HasLine"/> is false when no line could be measured confidently (a flat/blank frame, a dip
/// too shallow to trust, or one whose half-depth level is never crossed within the sampled window), in
/// which case the other fields are meaningless (0).
/// </summary>
public readonly record struct SpectralLineFocusStats(bool HasLine, double FwhmPixels, double DepthFraction, SpectralLineFocusDetail? Detail = null);

/// <summary>
/// What a caller needs to *draw* a <see cref="SpectralLineFocusAnalyzer"/> reading (the focus-aid graph):
/// the straightened line profile the measurement ran on (present even when no line was found - seeing why
/// is the point), and, where they could be worked out, the measurement's own reference points, all in the
/// profile's own units (positions in pixel-shift from the fitted line centre; levels in raw intensity).
/// <see cref="Continuum"/>/<see cref="HalfLevel"/> are null only when no dip could be characterized at all;
/// the two crossings are null unless <see cref="SpectralLineFocusStats.HasLine"/>.
/// </summary>
public sealed record SpectralLineFocusDetail(
    SpectralProfile Profile,
    double? MinimumShift,
    double? Continuum,
    double? HalfLevel,
    double? LeftCrossingShift,
    double? RightCrossingShift);

/// <summary>
/// Camera-focus aid - the counterpart to <see cref="FocusAnalyzer"/>'s collimator-focus edge width
/// (see CLAUDE.md's sunscan-app entry for why these are two distinct aids). Measures how sharply the
/// camera has resolved a spectral line: a narrower line means the sensor is in better optical focus,
/// independent of the collimator/spectrograph alignment.
///
/// Not a port of sunscan-backend's <c>calculate_fwhm</c>, which finds the span of samples at or above
/// half of a profile's *maximum* - right for a bright emission peak, wrong for a Fraunhofer *absorption*
/// line (a dark dip in a bright continuum), and it takes the profile of whatever rows the caller hands it
/// with no allowance for the spectrum's curvature. This instead:
/// <list type="number">
/// <item>fits the line's curvature ("smile") with the already-validated
/// <see cref="SpectralLineCurvatureDetector"/> and straightens the frame along it via
/// <see cref="SpectralProfileExtractor"/>, so curvature - which is a property of the optics, not the focus -
/// can't smear the profile and inflate the width;</item>
/// <item>finds the dip's minimum near the fitted line's own centre;</item>
/// <item>takes the local continuum as the *lower* of the highest values on either side of the dip
/// (within the sampled window), so half-depth is guaranteed to be crossable on both sides;</item>
/// <item>linearly interpolates the two half-depth crossings for a sub-pixel width.</item>
/// </list>
/// Depth-relative rather than absolute, so the width is stable across Gain/Exposure changes. Like
/// <see cref="EdgeFocusStats"/>, it's a pixel distance: comparable within one Binning/line, not across
/// different ones. Reports "no line" rather than a doubtful number whenever it can't measure
/// confidently - a wrong reading is worse than none while someone is chasing a minimum.
///
/// NOT YET VALIDATED against real hardware - the constants below are reasonable starting values, not
/// calibrated ones. The line locked onto is whichever the curvature detector picks (the darkest at the
/// frame's centre column), which for a wide view may not be the one the user cares most about; the
/// number is a consistent focus indicator either way as long as the same line stays selected.
/// </summary>
public static class SpectralLineFocusAnalyzer
{
    /// <summary>The sampled window is at most this many rows above/below the fitted line, but never
    /// less than <see cref="MinWindowHalfHeight"/> - wide enough for a line's wings to reach continuum,
    /// narrow enough that the extraction pass stays cheap on a full 2160-row sensor.</summary>
    private const double WindowHalfHeightFraction = 0.125;
    private const int MinWindowHalfHeight = 48;

    /// <summary>The dip's minimum is searched for within this fraction of the window either side of
    /// the fitted centre - the sub-pixel curvature fit lands very close to the true minimum, this is
    /// just slack for it not being exact.</summary>
    private const double MinimumSearchFraction = 0.1;
    private const int MinMinimumSearchRadius = 3;

    /// <summary>A dip shallower than this fraction of its own continuum isn't trusted as a real line.</summary>
    private const double MinDepthFraction = 0.03;

    /// <summary>Each side of the dip must extend at least this many times as far as its half-depth
    /// crossing - a Gaussian's wings reach continuum at roughly 2.5x that distance, so 2 is a lenient
    /// floor that still rejects a truncated window.</summary>
    private const double MinSideExtentOverHalfWidth = 2.0;

    /// <summary>Frames shorter than this have no meaningful dispersion axis to measure along.</summary>
    private const int MinFrameHeight = 9;

    /// <param name="curvature">An already-fitted curvature polynomial for <paramref name="frame"/> (full-frame
    /// column coordinates) - e.g. shared with the spectral overlay, or reused from a recent frame since the
    /// optics' "smile" doesn't change while focusing. Fitted here via <see cref="LiveCurvatureFitter"/> if null.</param>
    public static SpectralLineFocusStats Measure(CameraFrame frame, QuadraticPolynomial? curvature = null)
    {
        if (frame.Height < MinFrameHeight || frame.Width < 1)
        {
            return NoLine;
        }

        var polynomial = curvature ?? LiveCurvatureFitter.Fit(frame);
        return Measure(FrameConversion.ToFloatArray(frame), polynomial);
    }

    /// <summary>As <see cref="Measure(CameraFrame, QuadraticPolynomial?)"/>, for a caller that already holds the converted
    /// frame and its fitted curvature (e.g. to share them with another analysis).</summary>
    public static SpectralLineFocusStats Measure(float[,] frame, QuadraticPolynomial polynomial)
    {
        var height = frame.GetLength(0);
        if (height < MinFrameHeight || frame.GetLength(1) < 1)
        {
            return NoLine;
        }

        var halfHeight = System.Math.Max(1, (height / 2) - 1);
        var windowHalfHeight = System.Math.Min(halfHeight, System.Math.Max(MinWindowHalfHeight, (int)(height * WindowHalfHeightFraction)));
        var profile = SpectralProfileExtractor.Extract(frame, polynomial, windowHalfHeight);
        return MeasureProfile(profile);
    }

    /// <summary>The width measurement itself, on an already-straightened profile - public so it can be
    /// tested against exact synthetic shapes without going through curvature detection.</summary>
    public static SpectralLineFocusStats MeasureProfile(SpectralProfile profile)
    {
        var values = profile.Values;
        var n = values.Count;
        if (n < MinFrameHeight)
        {
            return NoLineFor(profile);
        }

        var centre = -profile.MinShiftPixels;
        var searchRadius = System.Math.Max(MinMinimumSearchRadius, (int)(n / 2 * MinimumSearchFraction));

        // The dip's minimum, nearest-to-centre first on ties (strict '<' scanning outward from the centre).
        var minIndex = -1;
        var minValue = double.MaxValue;
        for (var d = 0; d <= searchRadius; d++)
        {
            for (var side = d == 0 ? 0 : -1; side <= 1; side += 2)
            {
                var i = centre + (side * d);
                if (i < 0 || i >= n || double.IsNaN(values[i]))
                {
                    continue;
                }

                if (values[i] < minValue)
                {
                    minValue = values[i];
                    minIndex = i;
                }
            }
        }

        if (minIndex < 0)
        {
            return NoLineFor(profile);
        }

        // Highest value on each side, up to the first missing (NaN) sample or the window edge.
        var leftMax = double.MinValue;
        var leftExtent = 0;
        for (var i = minIndex - 1; i >= 0 && !double.IsNaN(values[i]); i--)
        {
            leftMax = System.Math.Max(leftMax, values[i]);
            leftExtent++;
        }

        var rightMax = double.MinValue;
        var rightExtent = 0;
        for (var i = minIndex + 1; i < n && !double.IsNaN(values[i]); i++)
        {
            rightMax = System.Math.Max(rightMax, values[i]);
            rightExtent++;
        }

        if (leftMax == double.MinValue || rightMax == double.MinValue)
        {
            return NoLineFor(profile); // the dip sits right at the edge of the sampled window - nothing to compare against
        }

        var continuum = System.Math.Min(leftMax, rightMax);
        if (continuum <= 0)
        {
            return NoLineFor(profile);
        }

        var depth = continuum - minValue;
        var minShift = profile.MinShiftPixels + minIndex;
        if (depth < continuum * MinDepthFraction)
        {
            return NoLineFor(profile, minShift, continuum);
        }

        var halfLevel = minValue + (depth / 2);
        var left = FindHalfCrossing(values, minIndex, -1, halfLevel);
        var right = FindHalfCrossing(values, minIndex, +1, halfLevel);
        if (left is not { } l || right is not { } r)
        {
            return NoLineFor(profile, minShift, continuum, halfLevel);
        }

        // A window truncated close to the dip (the line sits near the frame's top/bottom edge, so the
        // sampled rows run out) makes the "continuum" just whatever few samples were left, which can
        // yield a plausible-looking but far too narrow width - and a falsely small number would set a
        // false "best". Require each side to reach well past its own half-depth crossing.
        if (leftExtent < MinSideExtentOverHalfWidth * (minIndex - l) || rightExtent < MinSideExtentOverHalfWidth * (r - minIndex))
        {
            return NoLineFor(profile, minShift, continuum, halfLevel);
        }

        return new SpectralLineFocusStats(true, r - l, depth / continuum, new SpectralLineFocusDetail(
            profile, minShift, continuum, halfLevel, profile.MinShiftPixels + l, profile.MinShiftPixels + r));
    }

    private static SpectralLineFocusStats NoLine => new(false, 0, 0);

    private static SpectralLineFocusStats NoLineFor(SpectralProfile profile, double? minimumShift = null, double? continuum = null, double? halfLevel = null) =>
        new(false, 0, 0, new SpectralLineFocusDetail(profile, minimumShift, continuum, halfLevel, null, null));

    /// <summary>Walks from the dip's minimum in <paramref name="direction"/> to the first sample at or
    /// above <paramref name="level"/> and interpolates the sub-pixel crossing position; null if it
    /// runs off the window or into a missing sample first.</summary>
    private static double? FindHalfCrossing(IReadOnlyList<double> values, int start, int direction, double level)
    {
        var prev = start;
        for (var i = start + direction; i >= 0 && i < values.Count; i += direction)
        {
            var value = values[i];
            if (double.IsNaN(value))
            {
                return null;
            }

            if (value >= level)
            {
                var previousValue = values[prev];
                var t = (level - previousValue) / (value - previousValue);
                return prev + (t * direction);
            }

            prev = i;
        }

        return null;
    }
}
