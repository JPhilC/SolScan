namespace SolScan.Core.Camera;

/// <summary>
/// One frame's collimator-focus reading from <see cref="FocusAnalyzer.MeasureEdgeSteepness"/>.
/// <see cref="EdgeWidthPixels"/> is the number to *minimize* while dialing in the collimator - the
/// sub-pixel distance (in pixels) over which the profile actually transitions between its low and
/// high levels at the sharpest edge(s) found. A crisp, well-focused edge transitions over very few
/// pixels (a small width); a soft, defocused one smears the same transition over many more. Bounded
/// below by the optics/sensor's own real resolving limit - it can't usefully go to zero - and
/// deliberately not comparable in absolute terms to a different <see cref="ICameraDevice.Binning"/>
/// (binning changes the pixel scale itself), but stable across Gain/Exposure changes on the same
/// binning/ROI, unlike a raw gradient-magnitude metric would be, since it's a purely geometric
/// (threshold-crossing) measurement rather than an intensity one.
/// <see cref="HasEdge"/> is false when no edge could be measured *confidently* - a flat/blank frame,
/// one where the only apparent transition is too close to the frame's own border, or one with no
/// genuine flat plateau findable on both sides (see <see cref="FocusAnalyzer.FindPlateau"/>) - in
/// which case <see cref="EdgeWidthPixels"/> is meaningless (0). Reporting nothing is deliberate: see
/// the class doc comment for why a wrong number here is worse than no number.
/// <see cref="EdgeCount"/> is 1 for a single transition (the slit-edge case) or 2 for a disk crossing
/// the full frame width (both a rising and a falling edge found and both judged real - see
/// <see cref="FocusAnalyzer.IsSeparationTrustworthy"/>); <see cref="EdgeWidthPixels"/> is the average
/// across whichever of those were found.
/// </summary>
public readonly record struct EdgeFocusStats(bool HasEdge, double EdgeWidthPixels, int EdgeCount);

/// <summary>
/// Collimator-focus aid - see SolScan CLAUDE.md's "Collimator focus" note under Phase 4. Distinct
/// from the still-unbuilt camera-focus/FWHM aid (<c>calculate_fwhm</c>): this one measures the
/// sharpness of the solar disk's edges (or a single slit edge) rather than the spectral line's
/// width, so it reflects the collimator's alignment, not the camera sensor's own optical focus.
///
/// Builds one horizontal (spatial-axis, i.e. across the disk - see CLAUDE.md's "ROI height" note on
/// which axis is which in an SHG frame) intensity profile, then measures the sub-pixel width of that
/// profile's steepest edge transition(s) using the same 10%-90% threshold-crossing technique used to
/// characterize edge sharpness/MTF in real optical testing - a physical distance in pixels, not an
/// intensity-scale-dependent gradient magnitude, which is what makes it stable across Gain/Exposure
/// changes and meaningful on its own without a separate "% of range" figure.
///
/// This is now on its third design, each revision driven by a concrete real-hardware failure rather
/// than by guesswork:
///
/// 1. A raw two-point-gradient peak over a fixed 40-row sample (a direct port of sunscan-backend's
///    `focus_analyzer.py`) - replaced immediately since SolScan has no equivalent real-time Python/
///    Raspberry-Pi budget to justify staying that cheap.
/// 2. The sub-pixel threshold-crossing technique described above, but calibrated against the *whole
///    profile's* min/max and with the low/high reference levels taken from small windows a *fixed*
///    number of pixels either side of the edge. Real-hardware testing under a very noisy (heavy
///    Gain, overcast sky) scene showed this could report an "edge width" larger than the frame
///    itself - a global min/max is fragile against anything else unusual elsewhere in a wide frame,
///    and an unbounded threshold-crossing search just kept walking when that miscalibrated threshold
///    was never cleanly crossed nearby.
/// 3. The low/high reference levels are found by <see cref="FindPlateau"/> searching *outward* from
///    the edge until the profile actually goes flat (a small window with low internal variance),
///    however far that takes, rather than assuming a fixed distance. Fixed pixel distances (version
///    2's approach) don't generalize: a real optical edge's width in *pixels* scales with sensor
///    resolution and field of view, not with some universal constant, so a perfectly reasonable,
///    close-to-focus edge at a high native resolution can legitimately span more pixels than a small
///    fixed window ever reaches past - which is exactly what version 2's fixed 15px-gap/20px-window
///    design was seen to reject as "no edge" on a real, plausibly-close-to-focus capture. Searching
///    for a *verified* flat plateau instead removes the need to guess a correct fixed distance for a
///    given resolution/crop at all.
/// 4. Also found on the very same real capture, once (3) let a genuinely wide-but-real edge reach the
///    confidence-gating step at all: the original gate required each candidate's *peak two-point
///    gradient* to clear a fraction of the profile's range (and the weaker candidate to clear a
///    fraction of the stronger one). Peak two-point gradient is inherently smaller the more pixels a
///    transition of the same total amplitude is spread over - so that gate structurally penalized
///    exactly the wide edges (3) was just built to measure, rejecting this real capture's edge before
///    the plateau search even ran. Removed entirely: <see cref="IsSeparationTrustworthy"/>, comparing
///    the *actual achieved* low/high levels once <see cref="FindPlateau"/> has found them, is already
///    a strictly better confidence gate for this purpose - width/scale-invariant, unlike a derivative.
///
/// The profile itself is built from the *median* of each column's sampled rows rather than the mean
/// (see <see cref="BuildProfile"/>), and lightly median-smoothed along its width afterward (see
/// <see cref="MedianSmooth"/>) - both edge-preserving denoising steps (unlike a mean/box blur, a
/// median resists an isolated noisy/hot pixel or a thin unrelated feature - including the darker,
/// slightly curved spectral-line band an SHG wide view always shows - grazing a minority of the
/// sampled rows, without smearing a genuinely sharp transition the way a linear blur would).
///
/// Pure and stateless, like <see cref="FramePreview"/> - no UI/threading/session-state dependency of
/// its own (the "best value seen so far" high-water mark sunscan-app's own Focus assistant keeps is a
/// live-session UI concern, not part of this per-frame measurement - see CaptureViewModel).
/// </summary>
public static class FocusAnalyzer
{
    /// <summary>How many rows (evenly spread across the full frame height, not just the top/bottom
    /// extremes) are sampled per column before taking their median - see <see cref="BuildProfile"/>.
    /// More rows means a more representative profile, at a cost that's still trivial on a desktop CPU
    /// even at a live preview's frame rate.</summary>
    public const int DefaultProfileRowSampleCount = 200;

    /// <summary>Radius (in columns) of the median filter applied along the profile's width after
    /// row-sampling - see <see cref="MedianSmooth"/>. Deliberately small: it exists to reject an
    /// occasional bad column (e.g. a hot/dead pixel), not to blur genuine edge detail away.</summary>
    private const int SmoothingRadius = 2;

    /// <summary>The profile is normalized to its *local* [low, high] levels before thresholding (see
    /// <see cref="FindPlateau"/>) - these are fractions of that local range, not raw sensor values or
    /// fractions of the whole profile's own range.</summary>
    private const double LowThresholdFraction = 0.10;
    private const double HighThresholdFraction = 0.90;

    /// <summary>How far <see cref="FindPlateau"/> skips past a candidate edge before starting to look
    /// for a flat plateau - just enough to clear the transition itself.</summary>
    private const int PlateauMinGapFromEdge = 5;

    /// <summary>How many consecutive columns <see cref="FindPlateau"/> examines at each candidate
    /// position to judge flatness.</summary>
    private const int PlateauWindowSize = 15;

    /// <summary>How far <see cref="FindPlateau"/> advances its search position each step - a modest
    /// stride keeps the search cheap without needing to test every single column.</summary>
    private const int PlateauSearchStep = 5;

    /// <summary>A candidate window counts as a genuine flat plateau if its own internal (max - min) is
    /// below this fraction of the profile's overall range - loose enough to tolerate realistic sensor
    /// noise (this runs on an already row-median-denoised profile, but residual per-column noise is
    /// still real, especially at a high Gain), tight enough to reject a spot still mid-transition.</summary>
    private const double PlateauFlatnessFraction = 0.08;

    /// <summary>How far from the edge, as a fraction of the *frame's own width*, <see cref="FindPlateau"/>
    /// will search before giving up - scaling with the frame's width rather than a fixed pixel count is
    /// what lets this handle a genuinely wide, soft transition on a high-resolution capture without
    /// either guessing a bigger constant or leaving the search unbounded (see the class doc comment on
    /// why an earlier, truly unbounded search once caused runaway results on a noisy frame).</summary>
    private const double MaxPlateauSearchFraction = 0.3;

    /// <summary>Final sanity check once both sides' plateaus are found: they must differ by at least
    /// this fraction of the profile's overall range, so two coincidentally-similar "flat" spots (e.g.
    /// both still faintly trending in a very low-contrast, noisy scene) aren't mistaken for the real
    /// low/high levels. Looser than a fixed-window design would need, since <see cref="FindPlateau"/>
    /// has already verified each side is genuinely flat, not just "some distance away".</summary>
    private const double MinPlateauSeparationFraction = 0.25;

    /// <summary>
    /// See the class doc comment for the technique. <paramref name="frame"/>'s width is always used
    /// at full native resolution (sub-pixel edge width needs it); <paramref name="rowSampleCount"/>
    /// controls how many rows (evenly spread across the height) are sampled per column.
    /// </summary>
    public static EdgeFocusStats MeasureEdgeSteepness(CameraFrame frame, int rowSampleCount = DefaultProfileRowSampleCount)
    {
        var width = frame.Width;
        var height = frame.Height;

        // Not enough width for FindPlateau's own minimum search room on even one side - this
        // technique isn't meaningful below that scale.
        if (width * MaxPlateauSearchFraction < PlateauMinGapFromEdge + PlateauWindowSize || height < 1)
        {
            return new EdgeFocusStats(false, 0, 0);
        }

        var profile = MedianSmooth(BuildProfile(frame, rowSampleCount), SmoothingRadius);

        var min = profile[0];
        var max = profile[0];
        for (var x = 1; x < width; x++)
        {
            if (profile[x] < min)
            {
                min = profile[x];
            }
            if (profile[x] > max)
            {
                max = profile[x];
            }
        }

        var range = max - min;
        if (range <= 0)
        {
            return new EdgeFocusStats(false, 0, 0); // perfectly flat frame - nothing to measure
        }

        var gradient = ComputeGradient(profile);

        // First-occurrence argmax/argmin, matching numpy.argmax/argmin's own tie-breaking.
        var risingIndex = 0;
        var fallingIndex = 0;
        for (var x = 1; x < width; x++)
        {
            if (gradient[x] > gradient[risingIndex])
            {
                risingIndex = x;
            }
            if (gradient[x] < gradient[fallingIndex])
            {
                fallingIndex = x;
            }
        }

        // Signed, not Math.Abs: a profile with no genuine rising slope anywhere (e.g. a purely
        // falling single slit edge) has its *least negative* gradient value at risingIndex, which
        // must not be treated as a real rising edge just because it's the largest value present.
        //
        // Deliberately *not* gated here by how large these peak two-point gradients are (an earlier
        // version of this method required each candidate's peak gradient to clear a fraction of the
        // profile's range, and the weaker of the two to clear a fraction of the stronger one) - peak
        // two-point gradient is inherently *smaller* the more pixels a transition of the same total
        // amplitude is spread over, so that gate structurally penalized exactly the wide-but-genuine
        // edges this design exists to measure (seen concretely on real hardware: a plausible,
        // close-to-focus edge at full sensor resolution rejected as "no edge" purely because spreading
        // its amplitude over more pixels shrank its peak per-pixel slope below the threshold). These
        // two values are only used to locate *where* each candidate direction's steepest point is, and
        // to skip a direction with no slope at all (<= 0, the genuine single-slit-edge case - see
        // above); whether a candidate is *real* is decided entirely by <see cref="IsSeparationTrustworthy"/>
        // below, once its actual low/high plateau levels are known - a comparison of achieved amplitude,
        // which is scale/width-invariant in a way a derivative never is.
        var risingMagnitude = gradient[risingIndex];
        var fallingMagnitude = -gradient[fallingIndex];

        double widthSum = 0;
        var edgeCount = 0;

        if (risingMagnitude > 0 && MeasureRisingEdgeWidth(profile, risingIndex, range) is { } risingWidth)
        {
            widthSum += risingWidth;
            edgeCount++;
        }

        if (fallingMagnitude > 0 && MeasureFallingEdgeWidth(profile, fallingIndex, range) is { } fallingWidth)
        {
            widthSum += fallingWidth;
            edgeCount++;
        }

        return edgeCount == 0
            ? new EdgeFocusStats(false, 0, 0) // no candidate edge could be confidently characterized
            : new EdgeFocusStats(true, widthSum / edgeCount, edgeCount);
    }

    /// <summary>Builds one per-column profile from <paramref name="rowSampleCount"/> rows, evenly
    /// strided across the full frame height - the *median* of each column's sampled rows, not the
    /// mean, so an isolated hot/noisy pixel or a thin unrelated horizontal feature (a dust line, or
    /// part of the spectral line itself) grazing only a few of the sampled rows can't skew a
    /// column's value the way it would pull a mean.
    ///
    /// This is also what makes the spectral line(s) an SHG wide view *always* shows - darker, slightly
    /// curved ("smile") horizontal bands running across the whole frame - a non-issue without needing
    /// to detect and exclude them explicitly: at any single column, a line only occupies its own local
    /// thickness out of however many rows are sampled there. The curvature only changes *which* rows
    /// that is as you move across columns, not *how many* - so as long as the line's own thickness
    /// stays under half the sampled row height (true for any reasonably tall framing view - a genuinely
    /// science-grade, tightly-cropped-around-the-line ROI would be an exception), the median simply
    /// never sees it as anything but a small minority of its samples at every column, curvature
    /// included. Verified directly by <c>FocusAnalyzerTests.MeasureEdgeSteepness_
    /// IgnoresACurvedSpectralLineRunningThroughTheWholeFrame</c> rather than left as an assumption.</summary>
    private static double[] BuildProfile(CameraFrame frame, int rowSampleCount)
    {
        var width = frame.Width;
        var height = frame.Height;
        var bytesPerPixel = frame.BitDepth > 8 ? 2 : 1;
        var rowStride = width * bytesPerPixel;
        var data = frame.Data;

        var targetRows = Math.Max(1, Math.Min(rowSampleCount, height));
        var rowStep = Math.Max(1, height / targetRows);
        var sampledRows = 0;
        for (var y = 0; y < height; y += rowStep)
        {
            sampledRows++;
        }

        // Gathered row-major first (contiguous reads from the source frame, cache-friendly) into a
        // compact buffer, then each column's median is taken from it in a second pass.
        var samples = new double[sampledRows * width];
        var row = 0;
        for (var y = 0; y < height; y += rowStep)
        {
            var rowOffset = y * rowStride;
            var destOffset = row * width;
            for (var x = 0; x < width; x++)
            {
                var index = rowOffset + (x * bytesPerPixel);
                samples[destOffset + x] = bytesPerPixel == 2 ? data[index] | (data[index + 1] << 8) : data[index];
            }
            row++;
        }

        var profile = new double[width];
        var column = new double[sampledRows];
        for (var x = 0; x < width; x++)
        {
            for (var r = 0; r < sampledRows; r++)
            {
                column[r] = samples[(r * width) + x];
            }
            profile[x] = Median(column, sampledRows);
        }

        return profile;
    }

    /// <summary>Edge-preserving denoise: a sliding-window median filter of the given
    /// <paramref name="radius"/> along the profile's width. Unlike a mean/box blur, a median filter
    /// resists an isolated outlier column without smearing a genuinely sharp transition - exactly the
    /// property wanted here, since blurring the very thing being measured would defeat the point.</summary>
    private static double[] MedianSmooth(double[] profile, int radius)
    {
        if (radius <= 0)
        {
            return profile;
        }

        var n = profile.Length;
        var result = new double[n];
        var window = new double[(radius * 2) + 1];
        for (var x = 0; x < n; x++)
        {
            var count = 0;
            for (var d = -radius; d <= radius; d++)
            {
                var i = x + d;
                if (i >= 0 && i < n)
                {
                    window[count++] = profile[i];
                }
            }
            result[x] = Median(window, count);
        }

        return result;
    }

    /// <summary>Median of the first <paramref name="count"/> elements of <paramref name="values"/> -
    /// sorts that prefix in place (the caller's buffer is always fully overwritten before its next
    /// use, so this is safe to do without cloning).</summary>
    private static double Median(double[] values, int count)
    {
        Array.Sort(values, 0, count);
        return count % 2 == 1 ? values[count / 2] : (values[(count / 2) - 1] + values[count / 2]) / 2.0;
    }

    /// <summary>Central-difference gradient, matching numpy.gradient's default behaviour on a 1D
    /// array: forward/backward difference at the two endpoints, centred difference everywhere else.</summary>
    private static double[] ComputeGradient(double[] profile)
    {
        var n = profile.Length;
        var gradient = new double[n];

        gradient[0] = profile[1] - profile[0];
        gradient[n - 1] = profile[n - 1] - profile[n - 2];
        for (var x = 1; x < n - 1; x++)
        {
            gradient[x] = (profile[x + 1] - profile[x - 1]) / 2.0;
        }

        return gradient;
    }

    /// <summary>Sub-pixel width of a rising (low-to-high) transition centred near <paramref name="index"/> -
    /// the low side is to its left, the high side to its right. See <see cref="FindPlateau"/> for when
    /// this declines to measure at all.</summary>
    private static double? MeasureRisingEdgeWidth(double[] profile, int index, double globalRange)
    {
        var low = FindPlateau(profile, index, sideDirection: -1, globalRange);
        var high = FindPlateau(profile, index, sideDirection: +1, globalRange);
        if (low is not { } lowPlateau || high is not { } highPlateau || !IsSeparationTrustworthy(lowPlateau.Level, highPlateau.Level, globalRange))
        {
            return null;
        }

        var lowThreshold = lowPlateau.Level + ((highPlateau.Level - lowPlateau.Level) * LowThresholdFraction);
        var highThreshold = lowPlateau.Level + ((highPlateau.Level - lowPlateau.Level) * HighThresholdFraction);

        // The real crossing must lie somewhere between the edge and wherever the matching plateau was
        // actually found, plus a small margin - naturally scaled to whatever distance FindPlateau
        // needed, rather than a separate fixed cap.
        var xLow = FindCrossing(profile, index, direction: -1, lowThreshold, findAtOrBelow: true, lowPlateau.Distance + PlateauWindowSize);
        var xHigh = FindCrossing(profile, index, direction: +1, highThreshold, findAtOrBelow: false, highPlateau.Distance + PlateauWindowSize);
        return xLow is { } lo && xHigh is { } hi ? Math.Abs(hi - lo) : null;
    }

    /// <summary>See <see cref="MeasureRisingEdgeWidth"/> - a falling (high-to-low) transition, high
    /// side to the left of <paramref name="index"/>, low side to the right.</summary>
    private static double? MeasureFallingEdgeWidth(double[] profile, int index, double globalRange)
    {
        var high = FindPlateau(profile, index, sideDirection: -1, globalRange);
        var low = FindPlateau(profile, index, sideDirection: +1, globalRange);
        if (low is not { } lowPlateau || high is not { } highPlateau || !IsSeparationTrustworthy(lowPlateau.Level, highPlateau.Level, globalRange))
        {
            return null;
        }

        var lowThreshold = lowPlateau.Level + ((highPlateau.Level - lowPlateau.Level) * LowThresholdFraction);
        var highThreshold = lowPlateau.Level + ((highPlateau.Level - lowPlateau.Level) * HighThresholdFraction);

        var xHigh = FindCrossing(profile, index, direction: -1, highThreshold, findAtOrBelow: false, highPlateau.Distance + PlateauWindowSize);
        var xLow = FindCrossing(profile, index, direction: +1, lowThreshold, findAtOrBelow: true, lowPlateau.Distance + PlateauWindowSize);
        return xLow is { } lo && xHigh is { } hi ? Math.Abs(hi - lo) : null;
    }

    /// <summary>See <see cref="MinPlateauSeparationFraction"/> - true only if the two found plateaus
    /// are properly ordered and differ enough to trust as this edge's real low/high levels.</summary>
    private static bool IsSeparationTrustworthy(double lowLevel, double highLevel, double globalRange) =>
        highLevel > lowLevel && (highLevel - lowLevel) >= globalRange * MinPlateauSeparationFraction;

    /// <summary>One side's found flat reference level - <see cref="Level"/> is its median value,
    /// <see cref="Distance"/> how far from the edge (in columns) it took to find it, which
    /// <see cref="MeasureRisingEdgeWidth"/>/<see cref="MeasureFallingEdgeWidth"/> use to bound the
    /// matching threshold-crossing search on that side.</summary>
    private readonly record struct PlateauResult(double Level, int Distance);

    /// <summary>
    /// Searches outward from a candidate edge, in <paramref name="sideDirection"/> (-1 left, +1
    /// right), for the nearest point where the profile has genuinely gone flat - a window of
    /// <see cref="PlateauWindowSize"/> consecutive columns whose own internal range is below
    /// <see cref="PlateauFlatnessFraction"/> of <paramref name="globalRange"/> - and returns its
    /// median as that side's low/high reference level. Unlike assuming the plateau starts at some
    /// fixed distance (this design's previous approach - see the class doc comment for why that
    /// didn't generalize across resolutions), this adapts to however wide the real transition
    /// actually is, bounded only by <see cref="MaxPlateauSearchFraction"/> of the frame's own width.
    /// Returns null if no such flat window is found before running off the frame or past that bound -
    /// meaning this transition (or this side of it) doesn't reach a plateau within a reasonable
    /// fraction of the field of view, so it can't be characterized with confidence.
    /// </summary>
    private static PlateauResult? FindPlateau(double[] profile, int edgeIndex, int sideDirection, double globalRange)
    {
        var n = profile.Length;
        var maxDistance = (int)(n * MaxPlateauSearchFraction);
        var window = new double[PlateauWindowSize];

        for (var offset = PlateauMinGapFromEdge; offset + PlateauWindowSize <= maxDistance; offset += PlateauSearchStep)
        {
            var count = 0;
            for (var i = 0; i < PlateauWindowSize; i++)
            {
                var x = edgeIndex + (sideDirection * (offset + i));
                if (x < 0 || x >= n)
                {
                    count = 0;
                    break;
                }
                window[count++] = profile[x];
            }

            if (count < PlateauWindowSize)
            {
                break; // ran off the frame before filling a full window - no plateau reachable
            }

            var min = window[0];
            var max = window[0];
            for (var i = 1; i < count; i++)
            {
                if (window[i] < min)
                {
                    min = window[i];
                }
                if (window[i] > max)
                {
                    max = window[i];
                }
            }

            if (max - min <= globalRange * PlateauFlatnessFraction)
            {
                return new PlateauResult(Median(window, count), offset + (PlateauWindowSize / 2));
            }
        }

        return null;
    }

    /// <summary>
    /// Walks <paramref name="profile"/> from <paramref name="start"/> in <paramref name="direction"/>
    /// (+1/-1) looking for the first point at-or-past <paramref name="threshold"/> - "at or below" if
    /// <paramref name="findAtOrBelow"/>, "at or above" otherwise - and linearly interpolates between
    /// the last sample that hadn't yet crossed and the first one that has, for a sub-pixel crossing
    /// position. Returns null if the walk runs off the array, or past <paramref name="maxSteps"/>,
    /// without ever crossing - either means this transition can't be measured with confidence (see the
    /// class doc comment on why an unbounded search once caused wildly wrong results on noisy frames).
    /// </summary>
    private static double? FindCrossing(double[] profile, int start, int direction, double threshold, bool findAtOrBelow, int maxSteps)
    {
        var n = profile.Length;
        var prevIndex = start;
        var index = start + direction;
        var steps = 0;
        while (index >= 0 && index < n && steps < maxSteps)
        {
            var prevValue = profile[prevIndex];
            var value = profile[index];
            var met = findAtOrBelow ? value <= threshold : value >= threshold;
            if (met)
            {
                if (value == prevValue)
                {
                    return prevIndex;
                }
                var t = (threshold - prevValue) / (value - prevValue);
                return prevIndex + (t * direction);
            }
            prevIndex = index;
            index += direction;
            steps++;
        }
        return null;
    }
}
