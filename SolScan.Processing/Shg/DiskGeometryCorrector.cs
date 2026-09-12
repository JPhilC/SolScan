// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/sun/tasks/GeometryCorrector.java
// and .../sun/SolexVideoProcessor.java's `maybePerformFlips`/`maybePerformRotation`, and
// .../sun/crop/Cropper.java's call sites in .../expr/impl/Crop.java (Apache License, Version 2.0:
// http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE file for full attribution.
//
// JSolex applies the user's flip/rotation choices to the reconstructed image *before* ellipse fitting
// and geometry correction even run (as part of its own `rotateLeft`-then-flip-then-rotate pipeline
// stage) - this port does the same, just without the `rotateLeft` step itself: that step exists only
// to re-orient JSolex's own reconstructed image (spatial axis vertical) into the horizontal-spatial-axis
// convention the rest of its pipeline (banding correction, etc.) expects, and SolScan's own
// DiskReconstructor already produces images in that same orientation directly (rows = scan/time axis,
// columns = the SER frame's own spatial axis) - see SolScan CLAUDE.md's "ellipse fitting" phase entry.

using SolScan.Core.Processing;
using SolScan.Processing.Math;

namespace SolScan.Processing.Shg;

/// <summary>
/// Produces <see cref="GeneratedImageKind.GeometryCorrected"/> from a raw reconstructed disk image:
/// applies the requested mirror/rotation first (simple, exact pixel permutations - no interpolation
/// needed for either), fits an ellipse to the disk's edge (<see cref="DiskEdgeDetector"/>), warps it
/// circular (<see cref="GeometryResampler"/>), then autocrops per <see cref="AutocropMode"/>
/// (<see cref="DiskCropper"/>) if requested.
/// </summary>
public static class DiskGeometryCorrector
{
    /// <param name="Pixels">The corrected (and possibly cropped) image.</param>
    /// <param name="DetectedEllipse">The ellipse fitted to the disk, in the oriented-but-uncorrected
    /// image's coordinate system - before the shear/scale warp, not after.</param>
    /// <param name="TiltDegrees">The tilt angle the correction removed, in degrees - astro4j's own
    /// "detected geometry" info-panel figure (<c>GeometryDetectedEvent.tiltDegrees</c>); SolScan has
    /// no such panel yet, but the value costs nothing extra to carry for when it does.</param>
    /// <param name="XyRatio">The X/Y aspect ratio the correction detected (before any override -
    /// SolScan's trimmed <c>GeometryParams</c> has none yet) - the other half of that same info panel.</param>
    public readonly record struct Result(float[,] Pixels, Ellipse DetectedEllipse, double TiltDegrees, double XyRatio);

    /// <exception cref="InvalidOperationException">The disk's edge couldn't be fitted with an ellipse
    /// (see <see cref="DiskEdgeDetector.Detect"/>) - reported rather than silently skipped or faked,
    /// matching <see cref="FrameAverager"/>'s own "no frames exceeded the brightness threshold" stance
    /// on a similarly degenerate source.</exception>
    public static Result Correct(float[,] sourceImage, GeometryParams geometryParams, double maxPixelValue)
    {
        var oriented = ApplyMirror(sourceImage, geometryParams.HorizontalMirror, geometryParams.VerticalMirror);
        oriented = ApplyRotation(oriented, geometryParams.Rotation);

        var ellipse = DiskEdgeDetector.Detect(oriented, maxPixelValue)
            ?? throw new InvalidOperationException(
                "Could not fit an ellipse to the solar disk - not enough clear disk-edge samples were found in the reconstructed image.");

        var width = oriented.GetLength(1);
        var height = oriented.GetLength(0);
        var transform = GeometryTransform.Of(ellipse, forcedTilt: null, xyRatio: null, width, height, disallowDownsampling: false);
        var blackPoint = (float)(ImageStatistics.EstimateBlackPoint(oriented, ellipse) * 1.2);

        var corrected = GeometryResampler.ApplyCorrection(oriented, transform, blackPoint, maxPixelValue);
        var correctedCircle = GeometryResampler.ComputeCorrectedCircle(ellipse, transform);
        corrected = ApplyAutocrop(corrected, correctedCircle, geometryParams, blackPoint, width);

        var tiltDegrees = transform.Theta / System.Math.PI * 180;
        return new Result(corrected, ellipse, tiltDegrees, transform.DetectedRatio);
    }

    private static float[,] ApplyAutocrop(float[,] image, Ellipse correctedCircle, GeometryParams geometryParams, float blackPoint, int sourceWidth)
    {
        const int rounding = 16;
        return geometryParams.AutocropMode switch
        {
            AutocropMode.Off => image,
            AutocropMode.Radius1To1 => DiskCropper.CropToSquare(image, correctedCircle, blackPoint, 1.1, rounding).Cropped,
            AutocropMode.Radius1To2 => DiskCropper.CropToSquare(image, correctedCircle, blackPoint, 1.2, rounding).Cropped,
            AutocropMode.Radius1To5 => DiskCropper.CropToSquare(image, correctedCircle, blackPoint, 1.5, rounding).Cropped,
            AutocropMode.SourceWidth => CropToSourceWidthIfItFits(image, correctedCircle, blackPoint, sourceWidth),
            AutocropMode.FixedWidth => CropToFixedWidth(image, correctedCircle, blackPoint, geometryParams.FixedWidth ?? 1024),
            _ => image,
        };
    }

    /// <summary>Only crops if a square of the source width actually fits centred on the disk -
    /// otherwise leaves the image uncropped rather than cutting into the disk itself, matching
    /// astro4j's own "destructive.cannot.crop" guard.</summary>
    private static float[,] CropToSourceWidthIfItFits(float[,] image, Ellipse correctedCircle, float blackPoint, int targetWidth)
    {
        var (cx, cy) = correctedCircle.Center();
        var halfWidth = targetWidth / 2.0;
        if (cx - halfWidth < 0 || cy - halfWidth < 0 || cx + halfWidth > targetWidth || cy + halfWidth > targetWidth)
        {
            return image;
        }

        return DiskCropper.CropToRectangle(image, correctedCircle, blackPoint, targetWidth, targetWidth).Cropped;
    }

    private static float[,] CropToFixedWidth(float[,] image, Ellipse correctedCircle, float blackPoint, int fixedWidth) =>
        DiskCropper.CropToRectangle(image, correctedCircle, blackPoint, fixedWidth, fixedWidth).Cropped;

    /// <summary>Exact pixel permutation, matching astro4j's own <c>maybePerformFlips</c> (horizontal =
    /// mirror the spatial/x axis, vertical = mirror the scan/y axis) - a no-op copy when neither flag
    /// is set.</summary>
    private static float[,] ApplyMirror(float[,] image, bool horizontal, bool vertical)
    {
        if (!horizontal && !vertical)
        {
            return image;
        }

        var height = image.GetLength(0);
        var width = image.GetLength(1);
        var output = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            var sourceY = vertical ? height - y - 1 : y;
            for (var x = 0; x < width; x++)
            {
                var sourceX = horizontal ? width - x - 1 : x;
                output[y, x] = image[sourceY, sourceX];
            }
        }

        return output;
    }

    /// <summary>Exact 90-degree pixel permutation - matches astro4j's own <c>maybePerformRotation</c>
    /// (<c>Left</c> = counterclockwise, <c>Right</c> = clockwise); a no-op for <c>None</c>.</summary>
    private static float[,] ApplyRotation(float[,] image, RotationKind rotation)
    {
        if (rotation == RotationKind.None)
        {
            return image;
        }

        var height = image.GetLength(0);
        var width = image.GetLength(1);
        var output = new float[width, height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (rotation == RotationKind.Left)
                {
                    output[width - x - 1, y] = image[y, x];
                }
                else
                {
                    output[x, height - y - 1] = image[y, x];
                }
            }
        }

        return output;
    }
}
