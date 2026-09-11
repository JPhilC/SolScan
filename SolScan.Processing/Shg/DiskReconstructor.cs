// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/sun/SolexVideoProcessor.java
// (processSingleFrame, Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0).
// See SolScan's NOTICE file for full attribution.

using System.Threading;
using SolScan.Core.Capture;
using SolScan.Processing.Math;

namespace SolScan.Processing.Shg;

/// <summary>
/// Builds a disk image from a SER capture by taking, from each frame, the row at the studied line's
/// position (a fitted <see cref="QuadraticPolynomial"/> curve, offset by a pixel shift) and stacking
/// those rows across all frames. Each row is actually a 5-tap Gaussian-weighted blend
/// (<c>dy ∈ {-1, -0.5, 0, 0.5, 1}</c>) for anti-aliasing, linearly interpolating between the two
/// nearest source rows at each tap - direct port of <c>processSingleFrame</c>'s per-column loop.
/// SolScan has no "already-trimmed SER" fast path (unlike the original, which skips anti-aliasing
/// for those), so this always uses the full 5-tap version - the more thorough of the original's two
/// behaviours, not a cut corner.
/// </summary>
public sealed class DiskReconstructor
{
    private static readonly double[] DyTaps = [-1.0, -0.5, 0.0, 0.5, 1.0];

    /// <returns>Row-major <c>[frameIndex, x]</c> - width = the SER frame's own width (the spatial
    /// axis), height = the frame count (the scan-position/time axis).</returns>
    public float[,] Reconstruct(
        ISerReader reader,
        QuadraticPolynomial polynomial,
        double pixelShift,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var header = reader.Header;
        var width = header.Width;
        var height = header.Height;
        var frameCount = header.FrameCount;
        var output = new float[frameCount, width];

        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = FrameConversion.ToFloatArray(reader.ReadFrame(frameIndex));

            for (var x = 0; x < width; x++)
            {
                double value = 0;
                double weightSum = 0;
                var baseline = polynomial.Evaluate(x) + pixelShift;

                foreach (var dy in DyTaps)
                {
                    var yd = baseline + dy;
                    var y1 = (int)System.Math.Floor(yd);
                    var y2 = y1 + 1;

                    // Out-of-bounds taps are skipped entirely (not clamped to the edge row) - the
                    // normalization below only divides by the weight of taps actually in bounds, so
                    // edge columns get a renormalized blend of whichever taps were valid rather than
                    // an edge-clamped one. If none are in bounds, the pixel is written as 0.
                    if (y1 < 0 || y1 >= height || y2 < 0 || y2 >= height)
                    {
                        continue;
                    }

                    var frac = yd - y1;
                    var weight = System.Math.Exp(-0.5 * dy * dy);
                    var yValue = ((1 - frac) * source[y1, x]) + (frac * source[y2, x]);
                    value += weight * yValue;
                    weightSum += weight;
                }

                output[frameIndex, x] = weightSum > 0 ? (float)(value / weightSum) : 0f;
            }

            if (frameIndex % 100 == 0)
            {
                progress?.Report($"Reconstructing... {frameIndex}/{frameCount}");
            }
        }

        return output;
    }
}
