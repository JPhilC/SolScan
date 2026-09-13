// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/stretching/RangeExpansionStrategy.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Processing.Stretching;

/// <summary>Scales every pixel so the image's own current maximum lands exactly at
/// <paramref name="maxPixelValue"/> - the first step of <see cref="ClaheStrategy"/>.</summary>
public static class RangeExpansionStrategy
{
    public static void Stretch(float[,] data, double maxPixelValue)
    {
        var height = data.GetLength(0);
        var width = data.GetLength(1);
        var max = -double.MaxValue;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                max = System.Math.Max(max, data[y, x]);
            }
        }

        // Not in the original (which would divide by ~0 and produce Infinity/NaN): a defensive no-op
        // for a genuinely blank/all-zero input, consistent with this pipeline's existing "degenerate
        // input gets a graceful fallback, not a crash from an unguarded division" stance (see e.g.
        // BackgroundNeutralizer.BlindNeutralize's own fallback).
        if (max <= 0)
        {
            return;
        }

        var scale = maxPixelValue / max;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = (float)(scale * data[y, x]);
            }
        }
    }
}
