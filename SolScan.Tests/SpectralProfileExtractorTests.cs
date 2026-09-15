using SolScan.Processing.Math;
using SolScan.Processing.Spectrum;

namespace SolScan.Tests;

public class SpectralProfileExtractorTests
{
    [Fact]
    public void Extract_StraightLine_RecoversKnownGaussianDipShape()
    {
        const int width = 100;
        const int height = 60;
        const double trueRow = 30;
        const double background = 1000;
        const double lineDepth = 800;
        const double lineSigma = 2.0;

        var frame = BuildSyntheticFrame(width, height, _ => trueRow, background, lineDepth, lineSigma);
        var polynomial = new QuadraticPolynomial(0, 0, trueRow);

        var profile = SpectralProfileExtractor.Extract(frame, polynomial, maxShiftPixels: 20);

        // Shift 0 is the line's own centre - should read close to background - lineDepth (the dip's
        // minimum); a few sigma out, the dip has decayed away and the value should be close to the
        // flat background.
        Assert.InRange(profile.ValueAt(0)!.Value, background - lineDepth - 5, background - lineDepth + 5);
        Assert.InRange(profile.ValueAt(15)!.Value, background - 5, background + 5);
        Assert.InRange(profile.ValueAt(-15)!.Value, background - 5, background + 5);

        // Symmetric around the line's centre for a straight (non-curved) line with no x-variation.
        Assert.InRange(System.Math.Abs(profile.ValueAt(10)!.Value - profile.ValueAt(-10)!.Value), 0, 1e-6);
    }

    [Fact]
    public void Extract_CurvedLine_TracksTheCurveNotAFixedRow()
    {
        const int width = 100;
        const int height = 60;
        const double background = 1000;
        const double lineDepth = 800;
        double TrueRow(int x) => 30 + (0.001 * (x - 50) * (x - 50));

        var frame = BuildSyntheticFrame(width, height, TrueRow, background, lineDepth, lineSigma: 2.0);
        // A polynomial matching the true curve - the extractor should find the dip at shift 0
        // regardless of how the true row varies with x, since it follows the polynomial per column.
        var polynomial = new QuadraticPolynomial(0.001, -0.1, 30 + (0.001 * 2500)); // 30 + 0.001*(x-50)^2 expanded
        // A deliberately wrong, straight polynomial - what shift 0 would sample if the extractor
        // ignored curvature and just used the line's row at the frame's own centre column (x=50).
        var wrongPolynomial = new QuadraticPolynomial(0, 0, TrueRow(50));

        var profile = SpectralProfileExtractor.Extract(frame, polynomial, maxShiftPixels: 20);
        var wrongProfile = SpectralProfileExtractor.Extract(frame, wrongPolynomial, maxShiftPixels: 20);

        // Not asserting a tight numeric value here - linear (2-tap) interpolation of a narrow
        // synthetic Gaussian dip has real quantization error when the analytic minimum falls between
        // integer rows (this profile extractor isn't the 5-tap anti-aliased reconstruction
        // DiskReconstructor uses for actual image output, and doesn't need to be for a correlation
        // profile). What this test actually checks is that following the *curve* lands meaningfully
        // closer to the true dip than assuming a fixed straight row does - proving the extractor
        // genuinely tracks per-column curvature, not that interpolation is lossless.
        Assert.True(profile.ValueAt(0)!.Value < wrongProfile.ValueAt(0)!.Value,
            $"Following the curve ({profile.ValueAt(0)}) should read closer to the dip than a fixed straight row ({wrongProfile.ValueAt(0)}).");
        Assert.True(profile.ValueAt(0)!.Value < background - (lineDepth * 0.5), $"Expected a real dip near shift 0, got {profile.ValueAt(0)}.");
    }

    [Fact]
    public void Extract_ShiftWalkingOffFrame_ReportsNoValueRatherThanClamping()
    {
        const int width = 10;
        const int height = 20;
        var frame = BuildSyntheticFrame(width, height, _ => 10, background: 500, lineDepth: 0, lineSigma: 1);

        // A shift this large walks every column off the top of a 20-row frame from row 10.
        var profile = SpectralProfileExtractor.Extract(frame, new QuadraticPolynomial(0, 0, 10), maxShiftPixels: 15);

        Assert.Null(profile.ValueAt(15));
        Assert.Null(profile.ValueAt(-15));
        Assert.NotNull(profile.ValueAt(0));
    }

    [Fact]
    public void Extract_NegativeMaxShift_Throws()
    {
        var frame = new float[10, 10];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SpectralProfileExtractor.Extract(frame, new QuadraticPolynomial(0, 0, 5), maxShiftPixels: -1));
    }

    [Fact]
    public void Extract_ProfileBounds_MatchRequestedRange()
    {
        var frame = new float[10, 10];
        var profile = SpectralProfileExtractor.Extract(frame, new QuadraticPolynomial(0, 0, 5), maxShiftPixels: 7);

        Assert.Equal(-7, profile.MinShiftPixels);
        Assert.Equal(7, profile.MaxShiftPixels);
        Assert.Equal(15, profile.Values.Count);
    }

    private static float[,] BuildSyntheticFrame(int width, int height, Func<int, double> trueRow, double background, double lineDepth, double lineSigma)
    {
        var data = new float[height, width];
        for (var x = 0; x < width; x++)
        {
            var center = trueRow(x);
            for (var y = 0; y < height; y++)
            {
                var dy = y - center;
                var dip = lineSigma > 0 ? lineDepth * System.Math.Exp(-(dy * dy) / (2 * lineSigma * lineSigma)) : 0;
                data[y, x] = (float)(background - dip);
            }
        }

        return data;
    }
}
