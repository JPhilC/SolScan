// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/stretching/GammaStrategy.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution. The original's OpenCL/GPU path is dropped entirely - SolScan.Processing
// has no such dependency, and this CPU path is what it always falls back to anyway.

namespace SolScan.Processing.Stretching;

/// <summary>Normalizes an image to its own current maximum, applies a gamma power-law curve, then
/// stretches the result back out to <paramref name="maxPixelValue"/>.</summary>
public static class GammaStrategy
{
    public static void Stretch(float[,] data, double gamma, double maxPixelValue)
    {
        var height = data.GetLength(0);
        var width = data.GetLength(1);
        var max = 1e-7;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                max = System.Math.Max(max, data[y, x]);
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var normalized = data[y, x] / max;
                var corrected = System.Math.Pow(normalized, gamma);
                data[y, x] = (float)(corrected * maxPixelValue);
            }
        }
    }
}
