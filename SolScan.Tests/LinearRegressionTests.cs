using SolScan.Processing.Math;

namespace SolScan.Tests;

public class LinearRegressionTests
{
    [Fact]
    public void SecondOrderRegression_Unweighted_RecoversKnownQuadratic()
    {
        // y = 2x^2 + 3x + 1
        var points = new[] { -2.0, -1.0, 0.0, 1.0, 2.0, 3.0 }
            .Select(x => new Point2D(x, (2 * x * x) + (3 * x) + 1))
            .ToArray();

        var fit = LinearRegression.SecondOrderRegression(points);

        Assert.Equal(2.0, fit.A, precision: 6);
        Assert.Equal(3.0, fit.B, precision: 6);
        Assert.Equal(1.0, fit.C, precision: 6);
    }

    [Fact]
    public void SecondOrderRegression_Weighted_UniformWeightsMatchUnweighted()
    {
        var points = new[] { -2.0, -1.0, 0.0, 1.0, 2.0, 3.0 }
            .Select(x => new Point2D(x, (2 * x * x) + (3 * x) + 1))
            .ToArray();
        var weights = points.Select(_ => 1.0).ToArray();

        var fit = LinearRegression.SecondOrderRegression(points, weights);

        Assert.Equal(2.0, fit.A, precision: 6);
        Assert.Equal(3.0, fit.B, precision: 6);
        Assert.Equal(1.0, fit.C, precision: 6);
    }

    [Fact]
    public void SecondOrderRegression_Weighted_DownweightedOutlierHasLessInfluence()
    {
        // The true curve is y = 2x^2 + 3x + 1; the point at x=-2 is a deliberate outlier (9, not 3).
        var points = new[]
        {
            new Point2D(-2, 9),
            new Point2D(-1, 0),
            new Point2D(0, 1),
            new Point2D(1, 6),
            new Point2D(2, 15),
            new Point2D(3, 28),
        };
        var lowWeightOnOutlier = new[] { 0.001, 1.0, 1.0, 1.0, 1.0, 1.0 };

        var fit = LinearRegression.SecondOrderRegression(points, lowWeightOnOutlier);

        Assert.Equal(2.0, fit.A, precision: 1);
        Assert.Equal(3.0, fit.B, precision: 1);
    }
}
