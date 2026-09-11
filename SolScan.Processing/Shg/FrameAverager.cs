// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/sun/AverageImageCreator.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution. Ported as a sequential, single-threaded two-pass version (pass 1: true
// max mean over every frame; pass 2: accumulate qualifying frames) rather than the original's
// parallel-lane/sampled-max-mean version - same threshold rule and same "sum then divide" averaging,
// just without the JVM-specific parallel I/O batching (a performance detail, not an algorithm
// change).

using System.Threading;
using SolScan.Core.Capture;

namespace SolScan.Processing.Shg;

/// <summary>Averages the "bright enough" frames of a SER capture into one clean frame to detect the
/// spectral line against - frames whose mean intensity is at most half the brightest frame's mean are
/// excluded (they're presumably off the solar disk, e.g. idle time before/after the actual scan).</summary>
public sealed class FrameAverager
{
    public float[,] ComputeAverage(ISerReader reader, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var header = reader.Header;
        var frameCount = header.FrameCount;
        if (frameCount == 0)
        {
            throw new InvalidOperationException("The SER file has no frames.");
        }

        progress?.Report("Averaging frames (measuring brightness)...");
        var maxMean = 0.0;
        for (var i = 0; i < frameCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mean = FrameConversion.MeanOf(FrameConversion.ToFloatArray(reader.ReadFrame(i)));
            if (mean > maxMean)
            {
                maxMean = mean;
            }
        }

        var threshold = 0.5 * maxMean;
        var sum = new double[header.Height, header.Width];
        var acceptedCount = 0;

        progress?.Report("Averaging frames (accumulating)...");
        for (var i = 0; i < frameCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = FrameConversion.ToFloatArray(reader.ReadFrame(i));
            if (FrameConversion.MeanOf(frame) <= threshold)
            {
                continue;
            }

            for (var y = 0; y < header.Height; y++)
            {
                for (var x = 0; x < header.Width; x++)
                {
                    sum[y, x] += frame[y, x];
                }
            }

            acceptedCount++;
        }

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
