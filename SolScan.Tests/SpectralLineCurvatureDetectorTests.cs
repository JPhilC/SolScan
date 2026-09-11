using SolScan.Processing.Shg;

namespace SolScan.Tests;

public class SpectralLineCurvatureDetectorTests
{
    [Fact]
    public void Detect_FindsCurvedAbsorptionLine_WithinTolerance()
    {
        const int width = 100;
        const int height = 60;
        double TrueRow(int x) => 30 + (0.001 * (x - 50) * (x - 50));

        var frame = BuildSyntheticFrame(width, height, TrueRow, background: 1000, lineDepth: 800, lineSigma: 2.0);

        var polynomial = new SpectralLineCurvatureDetector().Detect(frame);

        for (var x = 10; x < width - 10; x += 10)
        {
            var expected = TrueRow(x);
            var actual = polynomial.Evaluate(x);
            Assert.True(Math.Abs(actual - expected) < 1.0, $"At x={x}: expected ~{expected:F2}, got {actual:F2}");
        }
    }

    [Fact]
    public void Detect_FindsStraightAbsorptionLine_WithinTolerance()
    {
        const int width = 80;
        const int height = 40;
        const double trueRow = 20;

        var frame = BuildSyntheticFrame(width, height, _ => trueRow, background: 500, lineDepth: 400, lineSigma: 1.5);

        var polynomial = new SpectralLineCurvatureDetector().Detect(frame);

        for (var x = 5; x < width - 5; x += 5)
        {
            Assert.True(Math.Abs(polynomial.Evaluate(x) - trueRow) < 0.5, $"At x={x}: expected ~{trueRow}, got {polynomial.Evaluate(x):F2}");
        }
    }

    /// <summary>A background plane with a Gaussian-shaped dark dip tracking <paramref name="trueRow"/>
    /// per column - a synthetic absorption line with a real (non-single-pixel) width, so the
    /// depth-weighted-centroid refinement has something meaningful to average over.</summary>
    private static float[,] BuildSyntheticFrame(int width, int height, Func<int, double> trueRow, double background, double lineDepth, double lineSigma)
    {
        var data = new float[height, width];
        for (var x = 0; x < width; x++)
        {
            var center = trueRow(x);
            for (var y = 0; y < height; y++)
            {
                var dy = y - center;
                var dip = lineDepth * Math.Exp(-(dy * dy) / (2 * lineSigma * lineSigma));
                data[y, x] = (float)(background - dip);
            }
        }

        return data;
    }
}
