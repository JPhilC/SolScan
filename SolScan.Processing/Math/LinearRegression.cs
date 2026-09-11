// Adapted from astro4j's math/src/main/java/me/champeau/a4j/math/regression/LinearRegression.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Processing.Math;

/// <summary>
/// Second-order (quadratic) least-squares regression - a closed-form solve of the normal equations
/// (sums of centered moments), not a generic matrix inversion; formulas unchanged from the original.
/// </summary>
public static class LinearRegression
{
    /// <summary>Unweighted fit - every point counts equally.</summary>
    public static QuadraticPolynomial SecondOrderRegression(IReadOnlyList<Point2D> points)
    {
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0, sumXXY = 0, sumXXX = 0, sumXXXX = 0;
        foreach (var point in points)
        {
            var x = point.X;
            var y = point.Y;
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumXY += x * y;
            sumXXX += x * x * x;
            sumXXXX += x * x * x * x;
            sumXXY += x * x * y;
        }

        var n = points.Count;
        var sxx = sumXX - (sumX * sumX / n);
        var sxy = sumXY - (sumX * sumY / n);
        var sxx2 = sumXXX - (sumX * sumXX / n);
        var sx2y = sumXXY - (sumXX * sumY / n);
        var sx2x2 = sumXXXX - (sumXX * sumXX / n);

        var denominator = (sxx * sx2x2) - (sxx2 * sxx2);
        var a = ((sx2y * sxx) - (sxy * sxx2)) / denominator;
        var b = ((sxy * sx2x2) - (sx2y * sxx2)) / denominator;
        var c = (sumY / n) - (b * sumX / n) - (a * sumXX / n);

        return new QuadraticPolynomial(a, b, c);
    }

    /// <summary>Weighted fit - <paramref name="weights"/> must be the same length as
    /// <paramref name="points"/>, aligned by index.</summary>
    public static QuadraticPolynomial SecondOrderRegression(IReadOnlyList<Point2D> points, IReadOnlyList<double> weights)
    {
        double sumW = 0, sumX = 0, sumY = 0, sumXX = 0, sumXY = 0, sumXXY = 0, sumXXX = 0, sumXXXX = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var x = points[i].X;
            var y = points[i].Y;
            var w = weights[i];
            sumW += w;
            sumX += w * x;
            sumY += w * y;
            sumXX += w * x * x;
            sumXY += w * x * y;
            sumXXX += w * x * x * x;
            sumXXXX += w * x * x * x * x;
            sumXXY += w * x * x * y;
        }

        var sxx = sumXX - (sumX * sumX / sumW);
        var sxy = sumXY - (sumX * sumY / sumW);
        var sxx2 = sumXXX - (sumX * sumXX / sumW);
        var sx2y = sumXXY - (sumXX * sumY / sumW);
        var sx2x2 = sumXXXX - (sumXX * sumXX / sumW);

        var denominator = (sxx * sx2x2) - (sxx2 * sxx2);
        var a = ((sx2y * sxx) - (sxy * sxx2)) / denominator;
        var b = ((sxy * sx2x2) - (sx2y * sxx2)) / denominator;
        var c = (sumY / sumW) - (b * sumX / sumW) - (a * sumXX / sumW);

        return new QuadraticPolynomial(a, b, c);
    }
}
