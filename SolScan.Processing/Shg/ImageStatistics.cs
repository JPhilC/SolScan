// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/sun/tasks/EllipseFittingTask.java
// (the private `statsOf`/`Stats` helper) and .../sun/workflow/AnalysisUtils.java (`estimateBlackPoint`,
// `estimateBackgroundLevel`) and .../util/Histogram.java (Apache License, Version 2.0:
// http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE file for full attribution.

using SolScan.Processing.Math;

namespace SolScan.Processing.Shg;

/// <summary>Basic per-image statistics used across the disk-edge-detection/geometry-correction
/// pipeline (<see cref="DiskEdgeDetector"/>, <see cref="BackgroundNeutralizer"/>).</summary>
public readonly record struct ImageStats(float Average, float StdDev, float Min, float Max, float MinNonZero);

/// <summary>Small, purpose-built statistics helpers for the ellipse-fitting/geometry-correction
/// pipeline - not a general-purpose imaging library, just what that pipeline needs.</summary>
public static class ImageStatistics
{
    public static ImageStats ComputeStats(float[,] data)
    {
        var height = data.GetLength(0);
        var width = data.GetLength(1);
        var min = float.MaxValue;
        var max = -float.MaxValue;
        var minNonZero = float.MaxValue;
        float sum = 0;
        var n = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = data[y, x];
                n++;
                sum += v;
                min = System.Math.Min(min, v);
                max = System.Math.Max(max, v);
                if (v > 0)
                {
                    minNonZero = System.Math.Min(minNonZero, v);
                }
            }
        }

        var average = sum / n;
        float stddevSum = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var d = data[y, x] - average;
                stddevSum += d * d;
            }
        }

        var stddev = (float)System.Math.Sqrt(stddevSum / (n - 1));
        return new ImageStats(average, stddev, min, max, minNonZero);
    }

    /// <summary>The average value of pixels outside <paramref name="ellipse"/>, weighted down the
    /// further they sit from disk-relative-to-image-size centre - background pixels right at the edge
    /// of the image are trusted more than ones near the disk, which are more likely contaminated by
    /// the disk's own glow/reflections.</summary>
    public static double EstimateBlackPoint(float[,] image, Ellipse ellipse)
    {
        var height = image.GetLength(0);
        var width = image.GetLength(1);
        var (cx, cy) = ellipse.Center();
        var blackEstimate = double.MaxValue;
        var count = 0;

        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                if (ellipse.IsWithin(x, y))
                {
                    continue;
                }

                var v = image[y, x];
                if (v > 0 && float.IsFinite(v))
                {
                    var offcenter = 2 * System.Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy))) / (width + height);
                    count++;
                    blackEstimate += (offcenter * v - blackEstimate) / count;
                }
            }
        }

        return count == 0 ? 0 : blackEstimate;
    }

    /// <summary>Estimates the background (sky/off-disk) level via histogram analysis: walks the
    /// histogram from the bright end looking for the sharp drop-off that marks the edge of the
    /// signal, then settles on the last of a run of buckets that don't climb back above it.</summary>
    public static float EstimateBackgroundLevel(float[,] data, int bins, double maxPixelValue)
    {
        var values = BuildHistogram(data, bins, maxPixelValue);
        var cur = values[0];
        var idx = 0;
        for (var i = 1; i < values.Length; i++)
        {
            idx = i + 1;
            var previous = cur;
            cur = values[i];
            if (cur < 0.5 * previous)
            {
                break;
            }
        }

        while (idx + 1 < values.Length && values[idx + 1] <= cur)
        {
            idx++;
            cur = values[idx];
        }

        return (float)(maxPixelValue * idx / bins);
    }

    private static int[] BuildHistogram(float[,] data, int bins, double maxPixelValue)
    {
        var buckets = new int[bins];
        var height = data.GetLength(0);
        var width = data.GetLength(1);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = System.Math.Clamp((int)System.Math.Round(data[y, x] * bins / maxPixelValue), 0, bins - 1);
                buckets[i]++;
            }
        }

        return buckets;
    }
}
