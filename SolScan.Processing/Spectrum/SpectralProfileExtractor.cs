using SolScan.Processing.Math;

namespace SolScan.Processing.Spectrum;

/// <summary>
/// Builds a <see cref="SpectralProfile"/> from an averaged frame and its already-fitted line-curvature
/// polynomial (<see cref="SolScan.Processing.Shg.FrameAverager"/>/
/// <see cref="SolScan.Processing.Shg.SpectralLineCurvatureDetector"/> - both already real, already
/// validated against a real capture, reused as-is rather than duplicated). New, original code, not a
/// port of anything: <see cref="SolScan.Processing.Shg.DiskReconstructor"/>'s own row extraction is a
/// close cousin (also walks rows near the fitted polynomial) but builds one reconstructed row per
/// *frame* for disk imaging; this instead builds one averaged intensity per pixel-*shift*, collapsing
/// the whole spatial (x) axis of a single frame into a 1D spectral profile.
/// </summary>
public static class SpectralProfileExtractor
{
    /// <param name="averagedFrame">Row-major <c>[y, x]</c>, rows = spectral/dispersion axis, columns =
    /// spatial axis - the same shape <see cref="SolScan.Processing.Shg.FrameAverager.ComputeAverage"/>
    /// returns and <see cref="SolScan.Processing.Shg.SpectralLineCurvatureDetector.Detect"/> already
    /// consumes.</param>
    /// <param name="polynomial">The fitted line-curvature polynomial - row position of the studied
    /// line at column x is <c>polynomial.Evaluate(x)</c>.</param>
    /// <param name="maxShiftPixels">How far above/below the fitted line's own centre row to sample -
    /// typically half the frame's own height for a full-frame (no-ROI) recording, since that's as far
    /// as there's any data to sample regardless.</param>
    public static SpectralProfile Extract(float[,] averagedFrame, QuadraticPolynomial polynomial, int maxShiftPixels)
    {
        if (maxShiftPixels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxShiftPixels), maxShiftPixels, "Must be non-negative.");
        }

        var height = averagedFrame.GetLength(0);
        var width = averagedFrame.GetLength(1);
        var values = new double[(2 * maxShiftPixels) + 1];

        for (var i = 0; i < values.Length; i++)
        {
            var shift = i - maxShiftPixels;
            double sum = 0;
            var count = 0;

            for (var x = 0; x < width; x++)
            {
                var y = polynomial.Evaluate(x) + shift;
                var y0 = (int)System.Math.Floor(y);
                var y1 = y0 + 1;

                // Both taps must be in bounds - a shift that walks off the frame's own top/bottom edge
                // at this column simply contributes nothing from that column, matching
                // DiskReconstructor's own "skip out-of-bounds taps entirely, don't clamp" convention.
                if (y0 < 0 || y1 >= height)
                {
                    continue;
                }

                var frac = y - y0;
                sum += ((1 - frac) * averagedFrame[y0, x]) + (frac * averagedFrame[y1, x]);
                count++;
            }

            values[i] = count > 0 ? sum / count : double.NaN;
        }

        return new SpectralProfile(values, -maxShiftPixels);
    }
}
