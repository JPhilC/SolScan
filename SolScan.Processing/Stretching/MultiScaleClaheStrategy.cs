// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/expr/impl/Clahe.java
// (`applyMultiScaleClahe`/`computeTileSizesForImage`/`computeTileSizes`/`multiScaleClaheChannel` -
// Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE file
// for full attribution. This is astro4j's "CLAHE2" contrast-enhancement mode: run the already-ported
// ClaheStrategy several times at different tile sizes (halving down from one derived from the disk's own
// diameter, or the image size if there's no ellipse) and average the results - multiple equalization
// scales blended together, rather than one fixed tile size's worth of local contrast. The original's RGB
// image branch (`clahe2`'s `RGBImage` case, running the same thing per-channel) is dropped entirely -
// SolScan.Processing has no colour image type at all, mono-only throughout.

using SolScan.Processing.Math;

namespace SolScan.Processing.Stretching;

public static class MultiScaleClaheStrategy
{
    public const int Bins = 256;
    public const int MaxLevels = 6;
    public const double DefaultClip = 1.5;

    /// <summary><paramref name="ellipse"/>, if given, sizes the starting (largest) tile off the disk's
    /// own diameter rather than the whole image - matching astro4j's own behaviour once a
    /// geometry-corrected image's ellipse is known.</summary>
    public static void Stretch(float[,] image, Ellipse? ellipse, double clip = DefaultClip)
    {
        var height = image.GetLength(0);
        var width = image.GetLength(1);
        var rawStartTileSize = ComputeRawStartTileSize(ellipse, width, height);
        var tileSizes = ComputeTileSizes(rawStartTileSize, Bins, MaxLevels);
        MultiScaleClaheChannel(image, width, height, tileSizes, clip);
    }

    private static int ComputeRawStartTileSize(Ellipse? ellipse, int width, int height)
    {
        if (ellipse is { } e)
        {
            var (semiA, semiB) = e.SemiAxis();
            var diameter = 2 * System.Math.Max(semiA, semiB);
            return (int)(diameter / 5);
        }

        return System.Math.Min(width, height) / 4;
    }

    /// <summary>Starts at the highest power of 2 not exceeding <paramref name="rawStartTileSize"/> (or
    /// <paramref name="bins"/>'s own minimum usable tile size, whichever is larger) and halves it down,
    /// collecting up to <paramref name="maxLevels"/> tile sizes, stopping once a tile would be too small
    /// to usefully bin into <paramref name="bins"/> histogram buckets.</summary>
    internal static List<int> ComputeTileSizes(int rawStartTileSize, int bins, int maxLevels)
    {
        var minTile = System.Math.Max(2, (int)System.Math.Ceiling(System.Math.Sqrt(bins)));
        var startTile = System.Math.Max(minTile, HighestOneBit(System.Math.Max(minTile, rawStartTileSize)));
        var tileSizes = new List<int>();
        var t = startTile;
        while (tileSizes.Count < maxLevels && t >= minTile)
        {
            tileSizes.Add(t);
            t /= 2;
        }

        if (tileSizes.Count == 0)
        {
            tileSizes.Add(minTile);
        }

        return tileSizes;
    }

    /// <summary>The largest power of 2 less than or equal to <paramref name="value"/> - matches Java's
    /// <c>Integer.highestOneBit</c> for a positive input (the only case this is ever called with).</summary>
    private static int HighestOneBit(int value)
    {
        if (value <= 0)
        {
            return 0;
        }

        var result = 1;
        while (result * 2 <= value)
        {
            result *= 2;
        }

        return result;
    }

    private static void MultiScaleClaheChannel(float[,] data, int width, int height, IReadOnlyList<int> tileSizes, double clip)
    {
        var sum = new float[height, width];
        foreach (var tileSize in tileSizes)
        {
            var copy = (float[,])data.Clone();
            new ClaheStrategy(tileSize, Bins, clip).Stretch(copy);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    sum[y, x] += copy[y, x];
                }
            }
        }

        var n = tileSizes.Count;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = sum[y, x] / n;
            }
        }
    }
}
