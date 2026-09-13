// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/stretching/LinearStrechingStrategy.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution. Distinct from the existing SolScan.Processing.Stretching.RangeExpansionStrategy
// (astro4j's own RangeExpansionStrategy), which only rescales an image's current *maximum* up to a
// target - this rescales by both the current minimum AND maximum, matching astro4j's own
// LinearStrechingStrategy exactly (needed by the new ArcsinhStretchingStrategy/Color.Colorize ports,
// both of which call astro4j's LinearStrechingStrategy.DEFAULT or a custom (lo, hi) instance).
// The original's GPU-not-worth-it comment on its own header is carried over unchanged: this is plain
// per-pixel arithmetic after one full-image min/max scan, not worth any GPU offload either.

namespace SolScan.Processing.Stretching;

/// <summary>Rescales every pixel linearly so the image's own current minimum maps to <paramref
/// name="lo"/> and its current maximum maps to <paramref name="hi"/> (a true min-max stretch, unlike
/// <see cref="RangeExpansionStrategy"/>'s "scale the max only" version) - a no-op if the image has no
/// dynamic range (min == max) at all.</summary>
public static class LinearStretchStrategy
{
    public static void Stretch(float[,] data, float lo, float hi)
    {
        var height = data.GetLength(0);
        var width = data.GetLength(1);
        var min = double.MaxValue;
        var max = -double.MaxValue;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = data[y, x];
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        var range = max - min;
        if (range == 0)
        {
            return;
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = (float)(hi / range * (data[y, x] - min));
            }
        }
    }

    /// <summary>astro4j's own <c>LinearStrechingStrategy.DEFAULT</c> - stretch to the full 16-bit
    /// container range.</summary>
    public static void StretchDefault(float[,] data) => Stretch(data, 0, 65535f);
}
