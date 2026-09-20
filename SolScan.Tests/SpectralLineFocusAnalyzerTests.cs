using SolScan.Core.Camera;
using SolScan.Processing.Spectrum;

namespace SolScan.Tests;

public class SpectralLineFocusAnalyzerTests
{
    private const double GaussianFwhmPerSigma = 2.354820045; // 2 * sqrt(2 * ln 2)

    private static SpectralProfile GaussianDipProfile(double sigma, double continuum = 20000, double depth = 8000, int halfSize = 50, double centreOffset = 0)
    {
        var values = new double[(2 * halfSize) + 1];
        for (var i = 0; i < values.Length; i++)
        {
            var shift = i - halfSize - centreOffset;
            values[i] = continuum - (depth * System.Math.Exp(-(shift * shift) / (2 * sigma * sigma)));
        }

        return new SpectralProfile(values, -halfSize);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(3.0)]
    [InlineData(6.0)]
    public void MeasureProfile_GaussianDip_RecoversTheAnalyticFwhm(double sigma)
    {
        var stats = SpectralLineFocusAnalyzer.MeasureProfile(GaussianDipProfile(sigma));

        Assert.True(stats.HasLine);
        // Sub-pixel linear interpolation of a 1px-sampled curve isn't exact - a tenth of a pixel is plenty.
        Assert.InRange(stats.FwhmPixels, (sigma * GaussianFwhmPerSigma) - 0.1, (sigma * GaussianFwhmPerSigma) + 0.1);
        Assert.Equal(8000.0 / 20000.0, stats.DepthFraction, precision: 2);
    }

    [Fact]
    public void MeasureProfile_DetailMatchesTheReportedNumbers_SoTheGraphShowsWhatWasMeasured()
    {
        var profile = GaussianDipProfile(sigma: 3);

        var stats = SpectralLineFocusAnalyzer.MeasureProfile(profile);

        var detail = Assert.IsType<SpectralLineFocusDetail>(stats.Detail);
        Assert.Same(profile, detail.Profile);
        Assert.Equal(stats.FwhmPixels, detail.RightCrossingShift!.Value - detail.LeftCrossingShift!.Value, precision: 6);
        Assert.Equal(0, detail.MinimumShift!.Value, precision: 6);
        // Half level sits exactly midway between the dip floor (12000) and the continuum.
        Assert.Equal((12000 + detail.Continuum!.Value) / 2, detail.HalfLevel!.Value, precision: 3);
    }

    [Fact]
    public void MeasureProfile_ANoLineResultStillCarriesTheProfileAndWhateverReferenceLevelsWereFound()
    {
        // 1% dip: rejected as too shallow, but the graph should still show the trace and the continuum.
        var stats = SpectralLineFocusAnalyzer.MeasureProfile(GaussianDipProfile(3, continuum: 20000, depth: 200));

        Assert.False(stats.HasLine);
        var detail = Assert.IsType<SpectralLineFocusDetail>(stats.Detail);
        Assert.NotNull(detail.Continuum);
        Assert.Null(detail.LeftCrossingShift);
        Assert.Null(detail.RightCrossingShift);
    }

    [Fact]
    public void MeasureProfile_SharperLineScoresSmallerWidth()
    {
        var sharp = SpectralLineFocusAnalyzer.MeasureProfile(GaussianDipProfile(sigma: 2));
        var soft = SpectralLineFocusAnalyzer.MeasureProfile(GaussianDipProfile(sigma: 5));

        Assert.True(sharp.HasLine && soft.HasLine);
        Assert.True(sharp.FwhmPixels < soft.FwhmPixels);
    }

    [Fact]
    public void MeasureProfile_IsIndependentOfBrightnessScale()
    {
        var dim = SpectralLineFocusAnalyzer.MeasureProfile(GaussianDipProfile(3, continuum: 2000, depth: 800));
        var bright = SpectralLineFocusAnalyzer.MeasureProfile(GaussianDipProfile(3, continuum: 40000, depth: 16000));

        Assert.Equal(dim.FwhmPixels, bright.FwhmPixels, precision: 3);
    }

    [Fact]
    public void MeasureProfile_FindsADipSlightlyOffCentre()
    {
        var stats = SpectralLineFocusAnalyzer.MeasureProfile(GaussianDipProfile(3, centreOffset: 2));

        Assert.True(stats.HasLine);
        Assert.Equal(3 * GaussianFwhmPerSigma, stats.FwhmPixels, precision: 1);
    }

    [Fact]
    public void MeasureProfile_FlatProfile_HasNoLine()
    {
        var stats = SpectralLineFocusAnalyzer.MeasureProfile(new SpectralProfile(Enumerable.Repeat(15000.0, 101).ToArray(), -50));

        Assert.False(stats.HasLine);
    }

    [Fact]
    public void MeasureProfile_TooShallowADip_HasNoLine()
    {
        // 1% deep - below MinDepthFraction, so not trusted as a real line.
        var stats = SpectralLineFocusAnalyzer.MeasureProfile(GaussianDipProfile(3, continuum: 20000, depth: 200));

        Assert.False(stats.HasLine);
    }

    [Fact]
    public void MeasureProfile_DipAtTheWindowEdge_HasNoLine()
    {
        // A monotonic ramp has its minimum at one end, with nothing on that side to compare against.
        var values = Enumerable.Range(0, 101).Select(i => 5000.0 + (i * 100)).ToArray();

        var stats = SpectralLineFocusAnalyzer.MeasureProfile(new SpectralProfile(values, -50));

        Assert.False(stats.HasLine);
    }

    [Fact]
    public void MeasureProfile_MissingSamplesBeforeHalfDepthIsReached_HasNoLine()
    {
        // The sampled window ran off the frame (NaN) on one side before the line recovered to half
        // depth - the crossing can't be located, so no reading rather than a guess.
        var profile = GaussianDipProfile(sigma: 8);
        var values = profile.Values.ToArray();
        for (var i = 54; i < values.Length; i++)
        {
            values[i] = double.NaN;
        }

        var stats = SpectralLineFocusAnalyzer.MeasureProfile(new SpectralProfile(values, profile.MinShiftPixels));

        Assert.False(stats.HasLine);
    }

    [Fact]
    public void Measure_CurvedLineInAFullFrame_IsNotInflatedByTheCurvature()
    {
        const int width = 200;
        const int height = 400;
        const double sigma = 3;

        // Same line, once straight and once with a strong "smile" (about 10px of row drift across the
        // width). The width along the dispersion axis is a property of the focus, not of the curvature,
        // so both must measure the same.
        var straight = SpectralLineFocusAnalyzer.Measure(BuildFrame(width, height, sigma, curvature: 0));
        var curved = SpectralLineFocusAnalyzer.Measure(BuildFrame(width, height, sigma, curvature: 0.001));

        Assert.True(straight.HasLine);
        Assert.True(curved.HasLine);
        Assert.Equal(sigma * GaussianFwhmPerSigma, straight.FwhmPixels, precision: 0);
        Assert.Equal(straight.FwhmPixels, curved.FwhmPixels, precision: 0);
    }

    [Fact]
    public void Measure_TinyFrame_HasNoLineRatherThanThrowing()
    {
        var frame = new CameraFrame(new byte[4 * 4 * 2], 4, 4, 16, DateTime.UtcNow);

        Assert.False(SpectralLineFocusAnalyzer.Measure(frame).HasLine);
    }

    /// <summary>A 16-bit frame with one Gaussian absorption line whose row drifts quadratically across the
    /// width (<paramref name="curvature"/> px of drift per px² of distance from the middle column).</summary>
    private static CameraFrame BuildFrame(int width, int height, double sigma, double curvature)
    {
        var data = new byte[width * height * 2];
        for (var x = 0; x < width; x++)
        {
            var dx = x - (width / 2.0);
            var lineRow = (height / 2.0) + (curvature * dx * dx);
            for (var y = 0; y < height; y++)
            {
                var offset = y - lineRow;
                var value = (ushort)(20000 - (8000 * System.Math.Exp(-(offset * offset) / (2 * sigma * sigma))));
                var index = ((y * width) + x) * 2;
                data[index] = (byte)value;
                data[index + 1] = (byte)(value >> 8);
            }
        }

        return new CameraFrame(data, width, height, 16, DateTime.UtcNow);
    }
}
