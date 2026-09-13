// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/util/Histogram.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution. Trimmed to the members AutoStretchStrategy actually uses
// (Of/Cumulative/Percentile) - ClaheStrategy deliberately does NOT reuse this type, since its own
// per-tile histogram uses a slightly different bin-index formula (divides by bins-1, not bins) than
// this type's Builder does; porting both formulas faithfully was judged more valuable than forcing a
// shared implementation astro4j itself doesn't have.

namespace SolScan.Processing.Stretching;

/// <summary>A simple fixed-bin histogram of image pixel values.</summary>
public sealed class Histogram
{
    public int[] Values { get; }

    public int PixelCount { get; }

    public Histogram(int[] values, int pixelCount)
    {
        Values = values;
        PixelCount = pixelCount;
    }

    public static Histogram Of(float[,] data, int bins, double maxPixelValue)
    {
        var height = data.GetLength(0);
        var width = data.GetLength(1);
        var buckets = new int[bins];
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var bin = System.Math.Clamp((int)System.Math.Round(data[y, x] * bins / maxPixelValue), 0, bins - 1);
                buckets[bin]++;
                count++;
            }
        }

        return new Histogram(buckets, count);
    }

    public Histogram Cumulative()
    {
        var cumulative = new int[Values.Length];
        var sum = 0;
        for (var i = 0; i < Values.Length; i++)
        {
            sum += Values[i];
            cumulative[i] = sum;
        }

        return new Histogram(cumulative, PixelCount);
    }

    /// <summary>The bin index at which the running count first reaches <paramref name="ratio"/> of
    /// <see cref="PixelCount"/> - call on a <see cref="Cumulative"/> histogram, matching astro4j's own
    /// usage (<c>cumulative.percentile(...)</c>, never a raw one).</summary>
    public int Percentile(double ratio)
    {
        var limit = (int)(PixelCount * ratio);
        for (var i = 0; i < Values.Length; i++)
        {
            if (Values[i] >= limit)
            {
                return i;
            }
        }

        return 0;
    }
}
