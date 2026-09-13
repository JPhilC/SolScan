// Adapted from astro4j's math/src/main/java/me/champeau/a4j/math/regression/Ellipse.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution. Trimmed to the members SolScan's geometry-correction pipeline actually
// uses (rotation angle, semi-axes, center, disk membership) - the original also carries vertex/
// tangent-line/rotate-and-flip/bounding-box helpers needed by JSolex's own rotateLeft/banding/
// active-region machinery, none of which SolScan has yet (see CLAUDE.md's "ellipse fitting" entry for
// why SolScan's own pipeline doesn't need an equivalent of astro4j's <c>rotateLeft</c> step at all).

namespace SolScan.Processing.Math;

/// <summary>
/// An ellipse in Cartesian conic form: <c>A*x^2 + B*x*y + C*y^2 + D*x + E*y + F = 0</c> - the result
/// of <see cref="EllipseRegression"/>, and the shape geometry correction (see
/// <c>SolScan.Processing.Shg.DiskGeometryCorrector</c>) reads the disk's tilt/aspect ratio/center from.
/// </summary>
public readonly record struct Ellipse(double A, double B, double C, double D, double E, double F)
{
    /// <summary>Formulas from https://mathworld.wolfram.com/Ellipse.html (used throughout this type).</summary>
    private double Discriminant => (B * B) - (4 * A * C);

    /// <summary>The ellipse's tilt angle, in radians.</summary>
    public double RotationAngle()
    {
        if (B == 0)
        {
            return A < C ? 0 : System.Math.PI / 2;
        }

        return A < C
            ? System.Math.Atan(B / (A - C)) / 2
            : (System.Math.PI / 2) + (System.Math.Atan(B / (A - C)) / 2);
    }

    public (double Cx, double Cy) Center()
    {
        var discriminant = Discriminant;
        var cx = (2 * C * D) - (B * E);
        var cy = (2 * A * E) - (B * D);
        return (cx / discriminant, cy / discriminant);
    }

    /// <summary>The semi-major and semi-minor axis lengths (in that order - the first is always the
    /// longer one, matching astro4j's own convention where <c>xyRatio = b/a &lt;= 1</c>).</summary>
    public (double A, double B) SemiAxis()
    {
        // Same parameter names as the mathworld page, to make it easy to cross-check against it.
        var a = A;
        var b = B / 2;
        var c = C;
        var d = D / 2;
        var f = E / 2;
        var g = F;
        var disc = (b * b) - (a * c);
        var num = 2 * ((a * f * f) + (c * d * d) + (g * b * b) - (2 * b * d * f) - (a * c * g));
        var z = System.Math.Sqrt(((a - c) * (a - c)) + (4 * b * b));
        var aPrime = System.Math.Sqrt(num / (disc * (z - (a + c))));
        var bPrime = System.Math.Sqrt(num / (disc * (-z - (a + c))));
        return (aPrime, bPrime);
    }

    public double XyRatio()
    {
        var (semiA, semiB) = SemiAxis();
        return semiB / semiA;
    }

    public bool IsWithin(double x, double y)
    {
        var value = (A * x * x) + (B * x * y) + (C * y * y) + (D * x) + (E * y) + F;
        return (A >= 0 && value <= 0) || (A <= 0 && value >= 0);
    }

    /// <summary>The x coordinate(s) on the ellipse for a given y, or null if there are none. When
    /// there's only one (the degenerate <c>A == 0</c> case), the second component is
    /// <see cref="double.NaN"/> - callers comparing against both components (e.g. distance-to-curve
    /// checks) get <see cref="double.NaN"/> back for that side, same as astro4j's own
    /// <c>Optional&lt;DoublePair&gt;</c> with a NaN second component.</summary>
    public (double X1, double X2)? FindX(double y)
    {
        var a = A;
        var b = (B * y) + D;
        var c = (C * y * y) + (E * y) + F;
        var disc = (b * b) - (4 * a * c);
        if (disc >= 0 && a != 0)
        {
            var sqrt = System.Math.Sqrt(disc);
            return ((-b + sqrt) / (2 * a), (-b - sqrt) / (2 * a));
        }

        if (a == 0 && b != 0)
        {
            return (-c / b, double.NaN);
        }

        return null;
    }

    /// <summary>The y coordinate(s) on the ellipse for a given x - see <see cref="FindX"/>.</summary>
    public (double Y1, double Y2)? FindY(double x)
    {
        var a = C;
        var b = (B * x) + E;
        var c = (A * x * x) + (D * x) + F;
        var disc = (b * b) - (4 * a * c);
        if (disc >= 0 && a != 0)
        {
            var sqrt = System.Math.Sqrt(disc);
            return ((-b + sqrt) / (2 * a), (-b - sqrt) / (2 * a));
        }

        if (a == 0 && b != 0)
        {
            return (-c / b, double.NaN);
        }

        return null;
    }

    /// <summary>Translates the ellipse's own curve by <c>(u, v)</c> in its current coordinate system -
    /// i.e. the returned ellipse's zero-set is the original's shifted by that vector (confirmed against
    /// astro4j's own real call sites, e.g. <c>Crop.java</c>'s <c>circle.translate(-left, -top)</c> when
    /// re-expressing a detected ellipse in a cropped image's coordinates). Used by
    /// <c>SolScan.Processing.Shg.DiskGeometryCorrector</c> to carry the corrected-circle ellipse through
    /// an autocrop.</summary>
    public Ellipse Translate(double u, double v) =>
        new(
            A,
            B,
            C,
            D - (2 * A * u) - (B * v),
            E - (2 * C * v) - (B * u),
            (A * u * u) + (B * u * v) + (C * v * v) - (D * u) - (E * v) + F);

    /// <summary>Rescales the ellipse about its own center by the given per-axis factors - e.g.
    /// <c>Rescale(1.05, 1.05)</c> for a small margin around the disk when excluding it from a
    /// background-fit sample region.</summary>
    public Ellipse Rescale(double scaleX, double scaleY)
    {
        var (cx, cy) = Center();
        var atOrigin = Translate(-cx, -cy);
        var scaleXSq = scaleX * scaleX;
        var scaleYSq = scaleY * scaleY;
        var scaleXY = scaleX * scaleY;
        var rescaled = new Ellipse(
            atOrigin.A * scaleYSq,
            atOrigin.B * scaleXY,
            atOrigin.C * scaleXSq,
            atOrigin.D * scaleX,
            atOrigin.E * scaleY,
            atOrigin.F * scaleXSq * scaleYSq);
        return rescaled.Translate(cx, cy);
    }

    /// <summary>The ellipse's axis-aligned bounding box, accounting for its own tilt.</summary>
    public (double MinX, double MaxX, double MinY, double MaxY) BoundingBox()
    {
        var (cx, cy) = Center();
        var (semiA, semiB) = SemiAxis();
        var theta = RotationAngle();
        var cosTheta = System.Math.Cos(theta);
        var sinTheta = System.Math.Sin(theta);
        var w = System.Math.Sqrt((semiA * semiA * cosTheta * cosTheta) + (semiB * semiB * sinTheta * sinTheta));
        var h = System.Math.Sqrt((semiA * semiA * sinTheta * sinTheta) + (semiB * semiB * cosTheta * cosTheta));
        return (cx - w, cx + w, cy - h, cy + h);
    }
}
