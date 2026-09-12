// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/util/GeometryTransform.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Processing.Math;

/// <summary>
/// The transformation which turns the elliptical solar disk of a raw reconstruction into a circular
/// one: every row is sheared and shifted, then both axes are scaled.
/// <para>
/// The same transformation has to be applied to the image pixels and to the ellipse itself, so it must
/// be computed once and shared instead of being derived again by every consumer -
/// <see cref="TransformX"/>/<see cref="TransformY"/> give the exact position of a source pixel in the
/// corrected image. The warp scales around the centre of the image rather than around the origin, and
/// both centres are truncated to integers, so the mapping carries the fixed offsets <see cref="OffsetX"/>/
/// <see cref="OffsetY"/> - they are sub-pixel but not zero, and belong to the transformation: computing
/// coordinates without them describes a warp which isn't the one applied to the pixels.
/// </para>
/// <para>
/// The transformation is tied to the width and height it was computed for, because the shift depends
/// on how far the shear displaces the last row, and the output dimensions derive from both.
/// </para>
/// </summary>
/// <param name="Theta">The tilt angle used, either forced or read from the ellipse.</param>
/// <param name="Shear">The horizontal shear applied to each row.</param>
/// <param name="Shift">The horizontal shift which keeps the sheared image within positive coordinates.</param>
/// <param name="Sx">The horizontal scaling factor.</param>
/// <param name="Sy">The vertical scaling factor.</param>
/// <param name="DetectedRatio">The X/Y ratio computed from the ellipse, before any user override.</param>
/// <param name="Width">The width of the image this transformation was computed for.</param>
/// <param name="Height">The height of the image this transformation was computed for.</param>
public readonly record struct GeometryTransform(
    double Theta,
    double Shear,
    double Shift,
    double Sx,
    double Sy,
    double DetectedRatio,
    int Width,
    int Height)
{
    /// <summary>Computes the transformation which makes the given ellipse circular.</summary>
    /// <param name="ellipse">The detected solar disk.</param>
    /// <param name="forcedTilt">Optional forced tilt angle (null to use the ellipse's own rotation) -
    /// SolScan's trimmed <c>GeometryParams</c> never sets this (no manual tilt override yet), but the
    /// parameter is kept so this stays a faithful, reusable port.</param>
    /// <param name="xyRatio">Optional forced X/Y ratio (null to use the detected one) - likewise
    /// unused by SolScan today.</param>
    /// <param name="width">The width of the image to correct.</param>
    /// <param name="height">The height of the image to correct.</param>
    /// <param name="disallowDownsampling">Whether the correction must stretch instead of shrinking -
    /// SolScan's trimmed <c>GeometryParams</c> has no such Advanced-tab setting, so callers pass
    /// <c>false</c> (allow downsampling), matching a sensible default.</param>
    public static GeometryTransform Of(
        Ellipse ellipse,
        double? forcedTilt,
        double? xyRatio,
        int width,
        int height,
        bool disallowDownsampling)
    {
        var theta = forcedTilt ?? ellipse.RotationAngle();
        var m = System.Math.Tan(-theta);
        var (a, b) = ellipse.SemiAxis();
        var cos = System.Math.Cos(theta);
        var sin = System.Math.Sin(theta);
        var shear = ((m * cos * a * a) + (sin * b * b)) / ((b * b * cos) - (a * a * m * sin));
        var maxDx = height * shear;
        var shift = maxDx < 0 ? maxDx : 0;
        var detectedRatio = System.Math.Abs(
            a * b * System.Math.Sqrt(((a * a * m * m) + (b * b)) / ((a * a * sin * sin) + (b * b * cos * cos)))
            / ((b * b * cos) - (a * a * m * sin)));
        var ratio = xyRatio ?? detectedRatio;

        double sx, sy;
        if (ratio < 1 || !disallowDownsampling)
        {
            sx = 1 / ratio;
            sy = 1.0;
        }
        else
        {
            sx = 1.0;
            sy = ratio;
        }

        return new GeometryTransform(theta, shear, shift, sx, sy, detectedRatio, width, height);
    }

    /// <summary>The width the source image occupies once sheared, before scaling.</summary>
    public int ExtendedWidth => Width + (int)System.Math.Ceiling(System.Math.Abs(Height * Shear));

    public int OutputWidth => (int)(ExtendedWidth * Sx);

    public int OutputHeight => (int)(Height * Sy);

    /// <summary>The horizontal offset introduced by scaling around the image centre, i.e.
    /// <c>outputWidth/2 - (extendedWidth/2)*sx</c> with both centres truncated to integers.</summary>
    public double OffsetX => (OutputWidth / 2) - (ExtendedWidth / 2 * Sx);

    /// <summary>The vertical offset introduced by scaling around the image centre, i.e.
    /// <c>outputHeight/2 - (height/2)*sy</c> with both centres truncated to integers.</summary>
    public double OffsetY => (OutputHeight / 2) - (Height / 2 * Sy);

    /// <summary>Transforms the abscissa of a point of the source image.</summary>
    public double TransformX(double x, double y) => ((x - Shift + (y * Shear)) * Sx) + OffsetX;

    /// <summary>Transforms the ordinate of a point of the source image.</summary>
    public double TransformY(double y) => (y * Sy) + OffsetY;
}
