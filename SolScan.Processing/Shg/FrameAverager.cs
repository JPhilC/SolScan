// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/sun/AverageImageCreator.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution. Ported as a two-pass version (pass 1: true max mean over every frame;
// pass 2: accumulate qualifying frames) rather than the original's sampled-max-mean version - same
// threshold rule and same "sum then divide" averaging, an algorithm-fidelity difference kept
// deliberately (see CLAUDE.md's "speeding up processing" investigation for why this wasn't changed).
// Both passes are parallelized across CPU cores (see ComputeAverage's own doc comment) - a pure
// execution-strategy change, not a port difference.

using System.Threading;
using System.Threading.Tasks;
using SolScan.Core.Capture;

namespace SolScan.Processing.Shg;

/// <summary>Averages the "bright enough" frames of a SER capture into one clean frame to detect the
/// spectral line against - frames whose mean intensity is at most half the brightest frame's mean are
/// excluded (they're presumably off the solar disk, e.g. idle time before/after the actual scan).</summary>
public sealed class FrameAverager
{
    /// <summary>Both passes below use <see cref="Parallel.For{TLocal}(int,int,Func{TLocal},Func{int,ParallelLoopState,TLocal,TLocal},Action{TLocal})"/> -
    /// a thread-local partial result (max mean, or a partial sum array) is merged into the shared one
    /// once per *thread* in <c>localFinally</c>, not once per *frame* - so parallelizing doesn't add
    /// per-frame locking overhead. Safe because <see cref="ISerReader.ReadFrame"/> is genuinely
    /// thread-safe (see <see cref="SolScan.Infrastructure.Capture.SerReader"/>'s own doc comment).</summary>
    public float[,] ComputeAverage(ISerReader reader, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var header = reader.Header;
        var frameCount = header.FrameCount;
        if (frameCount == 0)
        {
            throw new InvalidOperationException("The SER file has no frames.");
        }

        var parallelOptions = new ParallelOptions { CancellationToken = cancellationToken };

        progress?.Report("Averaging frames (measuring brightness)...");
        var maxMeanLock = new object();
        var maxMean = 0.0;
        Parallel.For(0, frameCount, parallelOptions,
            localInit: () => 0.0,
            body: (i, _, localMax) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mean = FrameConversion.MeanOf(FrameConversion.ToFloatArray(reader.ReadFrame(i, includeTimestamp: false)));
                return System.Math.Max(localMax, mean);
            },
            localFinally: localMax =>
            {
                lock (maxMeanLock)
                {
                    maxMean = System.Math.Max(maxMean, localMax);
                }
            });

        var threshold = 0.5 * maxMean;
        var sum = new double[header.Height, header.Width];
        var sumLock = new object();
        var acceptedCount = 0;

        progress?.Report("Averaging frames (accumulating)...");
        Parallel.For(0, frameCount, parallelOptions,
            localInit: () => new double[header.Height, header.Width],
            body: (i, _, localSum) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = FrameConversion.ToFloatArray(reader.ReadFrame(i, includeTimestamp: false));
                if (FrameConversion.MeanOf(frame) <= threshold)
                {
                    return localSum;
                }

                for (var y = 0; y < header.Height; y++)
                {
                    for (var x = 0; x < header.Width; x++)
                    {
                        localSum[y, x] += frame[y, x];
                    }
                }

                Interlocked.Increment(ref acceptedCount);
                return localSum;
            },
            localFinally: localSum =>
            {
                lock (sumLock)
                {
                    for (var y = 0; y < header.Height; y++)
                    {
                        for (var x = 0; x < header.Width; x++)
                        {
                            sum[y, x] += localSum[y, x];
                        }
                    }
                }
            });

        if (acceptedCount == 0)
        {
            throw new InvalidOperationException("No frames exceeded the brightness threshold - the recording may be entirely blank.");
        }

        var average = new float[header.Height, header.Width];
        var invCount = 1.0 / acceptedCount;
        for (var y = 0; y < header.Height; y++)
        {
            for (var x = 0; x < header.Width; x++)
            {
                average[y, x] = (float)(sum[y, x] * invCount);
            }
        }

        return average;
    }
}
