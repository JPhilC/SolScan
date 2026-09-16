// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/sun/SolexVideoProcessor.java
// (processSingleFrame, Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0).
// See SolScan's NOTICE file for full attribution.

using System.Threading;
using System.Threading.Tasks;
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
        CancellationToken cancellationToken = default) =>
        ReconstructMultiple(reader, polynomial, [pixelShift], progress, cancellationToken)[0];

    /// <summary>Reconstructs every one of <paramref name="pixelShifts"/> in a single pass over the
    /// file, rather than one call per shift each reading the whole file from scratch - each frame is
    /// decoded once and reused for every requested shift's own 5-tap blend. This is what actually
    /// matters for a multi-shift request (e.g. Raw + Continuum): re-reading a multi-GB capture once per
    /// requested shift is real, measured I/O cost, not a theoretical one - see <see cref="ShgProcessor"/>'s
    /// own doc comment / CLAUDE.md's "speeding up processing" investigation.
    ///
    /// Frames are processed in parallel across CPU cores (<see cref="Parallel.For(int,int,Action{int})"/>) -
    /// safe because <paramref name="reader"/>'s <see cref="ISerReader.ReadFrame"/> is genuinely
    /// thread-safe (see <see cref="SolScan.Infrastructure.Capture.SerReader"/>'s own doc comment) and
    /// every iteration only ever reads its own <c>frameIndex</c> and writes only to
    /// <c>outputs[s][frameIndex, x]</c> for every shift <c>s</c> - no two iterations ever touch the same
    /// memory, so no locking is needed for the reconstruction work itself.</summary>
    /// <returns>One row-major <c>[frameIndex, x]</c> output per entry of <paramref name="pixelShifts"/>,
    /// in the same order.</returns>
    public IReadOnlyList<float[,]> ReconstructMultiple(
        ISerReader reader,
        QuadraticPolynomial polynomial,
        IReadOnlyList<double> pixelShifts,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (pixelShifts.Count == 0)
        {
            throw new ArgumentException("At least one pixel shift is required.", nameof(pixelShifts));
        }

        var header = reader.Header;
        var width = header.Width;
        var height = header.Height;
        var frameCount = header.FrameCount;
        var outputs = new float[pixelShifts.Count][,];
        for (var s = 0; s < pixelShifts.Count; s++)
        {
            outputs[s] = new float[frameCount, width];
        }

        // Frames complete out of order under parallelism, so progress is reported off a completed-count
        // rather than the frame index itself.
        var completedCount = 0;

        Parallel.For(0, frameCount, new ParallelOptions { CancellationToken = cancellationToken }, frameIndex =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = FrameConversion.ToFloatArray(reader.ReadFrame(frameIndex, includeTimestamp: false));

            for (var x = 0; x < width; x++)
            {
                var baselineNoShift = polynomial.Evaluate(x);

                for (var s = 0; s < pixelShifts.Count; s++)
                {
                    double value = 0;
                    double weightSum = 0;
                    var baseline = baselineNoShift + pixelShifts[s];

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

                    outputs[s][frameIndex, x] = weightSum > 0 ? (float)(value / weightSum) : 0f;
                }
            }

            var completed = Interlocked.Increment(ref completedCount);
            if (completed % 100 == 0)
            {
                progress?.Report($"Reconstructing... {completed}/{frameCount}");
            }
        });

        return outputs;
    }
}
