// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/expr/impl/DiskFill.java
// (`doFillWithGradient(Ellipse, float[][], float)` and its private `computeSubpixelCoverage` helper -
// Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE file
// for full attribution. Only that one overload is ported - the two-colour
// (`insideColor`/`outsideColor`) overload, the hard-edged non-gradient `doFill`, and the whole
// `disk_fill`/`disk_mask` ImageMath-script-function wrapper around them (this type's real shape in
// astro4j) aren't: SolScan has no ImageMath scripting at all, and `Coronagraph` - the only caller so
// far - only ever needs a single-colour gradient fill.

using SolScan.Processing.Math;

namespace SolScan.Processing.Shg;

/// <summary>Fills the interior of a disk ellipse with a solid colour, antialiasing the boundary by
/// 4x4 subpixel sampling rather than a hard per-pixel cutoff - used by <see cref="Coronagraph"/> to
/// blank the solar disk itself before neutralizing/stretching whatever surrounds it.</summary>
public static class DiskFill
{
    private const int Samples = 4;

    /// <summary>Fills pixels inside <paramref name="ellipse"/> with <paramref name="fillColor"/> in
    /// place, blending at the boundary by how much of each edge pixel's own 4x4 subpixel grid falls
    /// inside; pixels entirely outside are left untouched.</summary>
    public static void FillWithGradient(Ellipse ellipse, float[,] image, float fillColor)
    {
        var height = image.GetLength(0);
        var width = image.GetLength(1);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var coverage = ComputeSubpixelCoverage(ellipse, x, y);
                if (coverage > 0.999f)
                {
                    image[y, x] = fillColor;
                }
                else if (coverage > 0.001f)
                {
                    image[y, x] = (fillColor * coverage) + (image[y, x] * (1 - coverage));
                }
            }
        }
    }

    private static float ComputeSubpixelCoverage(Ellipse ellipse, int px, int py)
    {
        var insideCount = 0;
        const int total = Samples * Samples;
        const float step = 1.0f / Samples;
        const float offset = step / 2.0f;

        for (var sy = 0; sy < Samples; sy++)
        {
            for (var sx = 0; sx < Samples; sx++)
            {
                var subX = px - 0.5 + offset + (sx * step);
                var subY = py - 0.5 + offset + (sy * step);
                if (ellipse.IsWithin(subX, subY))
                {
                    insideCount++;
                }
            }
        }

        return (float)insideCount / total;
    }
}
