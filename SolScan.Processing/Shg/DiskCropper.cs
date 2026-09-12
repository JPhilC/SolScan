// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/sun/crop/Cropper.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

using SolScan.Processing.Math;

namespace SolScan.Processing.Shg;

/// <summary>Crops a (by this point already geometry-corrected, so circular) solar disk to a square or
/// a fixed rectangle centred on it - the two shapes <see cref="AutocropMode"/> in
/// <c>SolScan.Core.Processing.GeometryParams</c> can ask for.</summary>
public static class DiskCropper
{
    public readonly record struct CropResult(float[,] Cropped, int OriginX, int OriginY);

    /// <param name="diameterFactor">How much bigger than the disk's own diameter the crop square
    /// should be (e.g. 1.2 for astro4j's <c>RADIUS_1_2</c>) - null falls back to the largest/smallest
    /// image dimension that still fits the disk, matching astro4j's own <c>autocrop</c> (not
    /// <c>autocrop2</c>) behaviour.</param>
    /// <param name="rounding">Rounds the computed square size to the nearest multiple of this - keeps
    /// output dimensions friendly to video encoders/binning, matching astro4j's own default of 16.</param>
    public static CropResult CropToSquare(float[,] image, Ellipse sunDisk, float blackPoint, double? diameterFactor, int rounding)
    {
        var height = image.GetLength(0);
        var width = image.GetLength(1);
        var (cx, cy) = sunDisk.Center();
        var (semiA, semiB) = sunDisk.SemiAxis();
        var diameter = semiA + semiB;

        var square = diameter > width || diameter > height ? System.Math.Max(width, height) : System.Math.Min(width, height);
        if (diameterFactor is { } factor)
        {
            square = (int)(diameter * factor);
            var remainder = square % rounding;
            if (remainder != 0)
            {
                var closestMultiple = remainder <= rounding / 2 ? -remainder : rounding - remainder;
                square += closestMultiple;
            }
        }

        var half = square / 2;
        var originX = CropOrigin(cx, half);
        var originY = CropOrigin(cy, half);
        return new CropResult(CopyInto(image, width, height, originX, originY, square, square, blackPoint), originX, originY);
    }

    public static CropResult CropToRectangle(float[,] image, Ellipse sunDisk, float blackPoint, int width, int height)
    {
        var sourceHeight = image.GetLength(0);
        var sourceWidth = image.GetLength(1);
        var (cx, cy) = sunDisk.Center();
        var originX = CropOrigin(cx, width / 2);
        var originY = CropOrigin(cy, height / 2);
        return new CropResult(CopyInto(image, sourceWidth, sourceHeight, originX, originY, width, height, blackPoint), originX, originY);
    }

    private static float[,] CopyInto(float[,] source, int sourceWidth, int sourceHeight, int originX, int originY, int width, int height, float blackPoint)
    {
        var cropped = new float[height, width];
        for (var yy = 0; yy < height; yy++)
        {
            for (var xx = 0; xx < width; xx++)
            {
                var sourceX = originX + xx;
                var sourceY = originY + yy;
                cropped[yy, xx] = sourceX >= 0 && sourceY >= 0 && sourceX < sourceWidth && sourceY < sourceHeight
                    ? source[sourceY, sourceX]
                    : blackPoint;
            }
        }

        return cropped;
    }

    private static int CropOrigin(double center, int halfSize) => (int)System.Math.Round(center) - halfSize;
}
