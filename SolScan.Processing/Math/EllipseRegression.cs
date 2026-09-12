// Adapted from astro4j's math/src/main/java/me/champeau/a4j/math/regression/EllipseRegression.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Processing.Math;

/// <summary>
/// Implements the numerically stable direct least-squares fitting of ellipses algorithm presented by
/// Radim Halir and Jan Flusser in https://autotrace.sourceforge.net/WSCG98.pdf.
/// <para>
/// The original builds explicit <c>Nx3</c> design matrices <c>D1</c>/<c>D2</c> and multiplies them
/// through a generic matrix library. Since <c>D1^T*D1</c>/<c>D1^T*D2</c>/<c>D2^T*D2</c> are all that's
/// ever needed from them, this instead accumulates those three 3x3 sums directly from the sample
/// points in one pass - mathematically identical, but avoids materializing the <c>Nx3</c> matrices or
/// needing a generic (non-3x3) matrix type at all, the same "direct sums instead of generic matrix
/// ops" style already used by <see cref="LinearRegression"/>'s own quadratic fit.
/// </para>
/// </summary>
public sealed class EllipseRegression
{
    /// <summary>The constant <c>C1</c> constraint matrix's inverse from the Halir-Flusser paper,
    /// <c>[[0,0,2],[0,-1,0],[2,0,0]]^-1</c>, computed once by hand rather than at every call.</summary>
    private static readonly Matrix3x3 ConstraintC1Inverse = new(
        0, 0, 0.5,
        0, -1, 0,
        0.5, 0, 0);

    private readonly IReadOnlyList<Point2D> _samples;

    public EllipseRegression(IReadOnlyList<Point2D> samples)
    {
        _samples = samples;
    }

    /// <summary>Solves the ellipse-fitting problem.</summary>
    /// <exception cref="InvalidOperationException">No eigenvector of the reduced system satisfies the
    /// ellipse-specific constraint (<c>4*A*C - B^2 &gt; 0</c>) - the samples don't admit an ellipse
    /// solution (e.g. too few, too collinear, or otherwise degenerate).</exception>
    public Ellipse Solve()
    {
        double sumX2 = 0, sumXy = 0, sumY2 = 0, sumX3 = 0, sumX2Y = 0, sumXy2 = 0, sumY3 = 0;
        double sumX4 = 0, sumX3Y = 0, sumX2Y2 = 0, sumXy3 = 0, sumY4 = 0;
        double sumX = 0, sumY = 0, count = _samples.Count;

        foreach (var p in _samples)
        {
            var x = p.X;
            var y = p.Y;
            var x2 = x * x;
            var y2 = y * y;
            sumX += x;
            sumY += y;
            sumX2 += x2;
            sumXy += x * y;
            sumY2 += y2;
            sumX3 += x2 * x;
            sumX2Y += x2 * y;
            sumXy2 += x * y2;
            sumY3 += y2 * y;
            sumX4 += x2 * x2;
            sumX3Y += x2 * x * y;
            sumX2Y2 += x2 * y2;
            sumXy3 += x * y2 * y;
            sumY4 += y2 * y2;
        }

        // s1 = D1^T*D1 where D1's rows are (x^2, xy, y^2); s2 = D1^T*D2 where D2's rows are (x, y, 1);
        // s3 = D2^T*D2.
        var s1 = new Matrix3x3(
            sumX4, sumX3Y, sumX2Y2,
            sumX3Y, sumX2Y2, sumXy3,
            sumX2Y2, sumXy3, sumY4);
        var s2 = new Matrix3x3(
            sumX3, sumX2Y, sumX2,
            sumX2Y, sumXy2, sumXy,
            sumXy2, sumY3, sumY2);
        var s3 = new Matrix3x3(
            sumX2, sumXy, sumX,
            sumXy, sumY2, sumY,
            sumX, sumY, count);

        var t = -s3.Inverse() * s2.Transpose();
        var m = ConstraintC1Inverse * (s1 + (s2 * t));

        foreach (var (_, vector) in m.SolveRealEigenpairs())
        {
            var a = vector[0];
            var b = vector[1];
            var c = vector[2];
            if ((4 * a * c) - (b * b) > 0)
            {
                var def = t.Multiply([a, b, c]);
                return new Ellipse(a, b, c, def[0], def[1], def[2]);
            }
        }

        throw new InvalidOperationException("Unable to find an ellipse solution for the given samples.");
    }
}
