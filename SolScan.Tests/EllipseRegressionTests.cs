using SolScan.Processing.Math;

namespace SolScan.Tests;

public class EllipseRegressionTests
{
    [Fact]
    public void Solve_PointsOnAKnownCircle_RecoversCenterAndRadius()
    {
        const double cx = 50;
        const double cy = 30;
        const double radius = 20;
        var points = Enumerable.Range(0, 40)
            .Select(i => i * 2 * System.Math.PI / 40)
            .Select(angle => new Point2D(cx + (radius * System.Math.Cos(angle)), cy + (radius * System.Math.Sin(angle))))
            .ToList();

        var ellipse = new EllipseRegression(points).Solve();

        var (fitCx, fitCy) = ellipse.Center();
        Assert.Equal(cx, fitCx, precision: 3);
        Assert.Equal(cy, fitCy, precision: 3);

        var (semiA, semiB) = ellipse.SemiAxis();
        Assert.Equal(radius, semiA, precision: 3);
        Assert.Equal(radius, semiB, precision: 3);
        Assert.Equal(1.0, ellipse.XyRatio(), precision: 3);
    }

    [Fact]
    public void Solve_PointsOnAKnownAxisAlignedEllipse_RecoversSemiAxesAndZeroTilt()
    {
        const double cx = 10;
        const double cy = -5;
        const double semiX = 40;
        const double semiY = 25;
        var points = Enumerable.Range(0, 60)
            .Select(i => i * 2 * System.Math.PI / 60)
            .Select(angle => new Point2D(cx + (semiX * System.Math.Cos(angle)), cy + (semiY * System.Math.Sin(angle))))
            .ToList();

        var ellipse = new EllipseRegression(points).Solve();

        Assert.Equal(0.0, ellipse.RotationAngle(), precision: 3);

        var (semiA, semiB) = ellipse.SemiAxis();
        var lengths = new[] { semiA, semiB }.OrderDescending().ToArray();
        Assert.Equal(semiX, lengths[0], precision: 2);
        Assert.Equal(semiY, lengths[1], precision: 2);
    }

    [Fact]
    public void Solve_ATiltedEllipse_RecoversTheTiltAngle()
    {
        const double semiA = 30;
        const double semiB = 15;
        var tilt = System.Math.PI / 6; // 30 degrees
        var cos = System.Math.Cos(tilt);
        var sin = System.Math.Sin(tilt);
        var points = Enumerable.Range(0, 60)
            .Select(i => i * 2 * System.Math.PI / 60)
            .Select(angle =>
            {
                var x = semiA * System.Math.Cos(angle);
                var y = semiB * System.Math.Sin(angle);
                return new Point2D((x * cos) - (y * sin), (x * sin) + (y * cos));
            })
            .ToList();

        var ellipse = new EllipseRegression(points).Solve();

        // rotationAngle() only ever returns an angle in [0, pi/2) (see its own branching on A<C) -
        // a tilted ellipse's *reported* angle can therefore land pi/2 away from the geometric tilt
        // used to build the samples, depending on which axis came out as the conic's own "a" term;
        // what matters is that the underlying physical axis is recovered, mod pi/2.
        var reported = ellipse.RotationAngle();
        var difference = System.Math.Abs(reported - tilt) % (System.Math.PI / 2);
        var wrapped = System.Math.Min(difference, (System.Math.PI / 2) - difference);
        Assert.True(wrapped < 0.01, $"Expected an angle within 0.01 rad of {tilt} (mod pi/2), got {reported}");
    }

    [Fact]
    public void Solve_TooFewCollinearPoints_ThrowsInvalidOperationException()
    {
        var points = new[] { new Point2D(0, 0), new Point2D(1, 1), new Point2D(2, 2) };

        Assert.Throws<InvalidOperationException>(() => new EllipseRegression(points).Solve());
    }
}
