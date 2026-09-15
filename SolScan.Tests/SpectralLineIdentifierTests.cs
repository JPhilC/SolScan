using SolScan.Core.Equipment;
using SolScan.Core.Processing;
using SolScan.Processing.Spectrum;

namespace SolScan.Tests;

public class SpectralLineIdentifierTests
{
    private static readonly SpectrographProfile Instrument = SpectrographProfile.CreateSolEx();
    private const double PixelSizeMicrons = 2.0;

    /// <summary>A primary dip plus a smaller companion dip offset to one side - genuinely
    /// distinguishable from another such shape by cross-correlation, unlike two plain symmetric
    /// Gaussians of different widths (which correlate strongly against each other regardless, since a
    /// Gaussian is nearly self-similar under rescaling - confirmed the hard way: an earlier version of
    /// this test used single symmetric Gaussians for the two candidate shapes and found Ca K still
    /// scored 0.94 against an H-alpha-shaped profile, not a genuine test of discrimination). Real named
    /// lines are this distinctive in practice too - see the real-bundled-window regression test below,
    /// which passes cleanly against actual BASS2000 data precisely because a real line's surrounding
    /// profile isn't a plain symmetric Gaussian either.</summary>
    private static double HAlphaLikeShape(double offsetAngstroms) =>
        9000
        - (3000 * System.Math.Exp(-(offsetAngstroms * offsetAngstroms) / (2 * 0.3 * 0.3)))
        - (600 * System.Math.Exp(-System.Math.Pow(offsetAngstroms - 1.2, 2) / (2 * 0.15 * 0.15)));

    private static double CalciumKLikeShape(double offsetAngstroms) =>
        9000
        - (1000 * System.Math.Exp(-(offsetAngstroms * offsetAngstroms) / (2 * 0.6 * 0.6)))
        - (900 * System.Math.Exp(-System.Math.Pow(offsetAngstroms + 0.8, 2) / (2 * 0.3 * 0.3)));

    [Fact]
    public void Identify_MatchesTheCorrectLineAmongDistinctCandidates_WithRealMargin()
    {
        // Two synthetic reference windows, at two of the real named lines' own wavelengths, with
        // deliberately distinct (asymmetric two-dip) shapes so a correct match should be unambiguous.
        var hAlphaWindow = BuildSyntheticWindow(SpectralRay.HAlpha.WavelengthAngstroms, HAlphaLikeShape);
        var calciumKWindow = BuildSyntheticWindow(SpectralRay.CalciumK.WavelengthAngstroms, CalciumKLikeShape);

        var dispersion = SpectralDispersion.ComputeAngstromsPerPixel(Instrument, SpectralRay.HAlpha.WavelengthAngstroms, PixelSizeMicrons);
        var observed = BuildObservedProfile(dispersion, maxShiftPixels: 100, HAlphaLikeShape, noise: 5);

        var identifier = new SpectralLineIdentifier(Instrument, PixelSizeMicrons, referenceWindows: [hAlphaWindow, calciumKWindow]);
        var result = identifier.Identify(observed);

        Assert.Equal(SpectralRay.HAlpha, result.IdentifiedRay);
        Assert.True(result.BestScore > 0.9, $"Expected a strong match, got score {result.BestScore}.");

        var caKScore = result.AllCandidates.Single(c => c.Ray == SpectralRay.CalciumK).Score;
        Assert.True(result.BestScore - caKScore > SpectralLineIdentifier.MinMarginOverRunnerUp,
            $"Expected a real margin over the wrong candidate: H-alpha={result.BestScore}, Ca K={caKScore}.");
    }

    [Fact]
    public void Identify_FlatNoisyProfile_ReportsNoConfidentMatch()
    {
        var hAlphaWindow = BuildSyntheticWindow(SpectralRay.HAlpha.WavelengthAngstroms, HAlphaLikeShape);
        var calciumKWindow = BuildSyntheticWindow(SpectralRay.CalciumK.WavelengthAngstroms, CalciumKLikeShape);

        // No real dip anywhere - just noise around a flat background, matching neither candidate's shape.
        var random = new Random(42);
        var values = Enumerable.Range(0, 201).Select(_ => 9000.0 + (random.NextDouble() * 40 - 20)).ToArray();
        var observed = new SpectralProfile(values, MinShiftPixels: -100);

        var identifier = new SpectralLineIdentifier(Instrument, PixelSizeMicrons, referenceWindows: [hAlphaWindow, calciumKWindow]);
        var result = identifier.Identify(observed);

        Assert.Null(result.IdentifiedRay);
    }

    [Fact]
    public void Identify_AgainstTheRealBundledHAlphaWindow_MatchesHAlpha()
    {
        // Uses the real embedded resource (no referenceWindows override) - a regression test that the
        // bundled reference-window resource actually loads and round-trips correctly, not just that
        // the algorithm's own math works on hand-built synthetic windows.
        var bundled = ReferenceWindowResource.LoadEmbedded();
        var realHAlphaWindow = bundled.Single(w => System.Math.Abs(w.CenterWavelengthAngstroms - SpectralRay.HAlpha.WavelengthAngstroms) < 0.01);

        var dispersion = SpectralDispersion.ComputeAngstromsPerPixel(Instrument, SpectralRay.HAlpha.WavelengthAngstroms, PixelSizeMicrons);
        var maxShiftPixels = (int)(realHAlphaWindow.StepAngstroms * (realHAlphaWindow.Intensities.Count - 1) / 2.0 / dispersion) - 5;

        // Sample the real bundled window itself (via linear interpolation) to build the observed
        // profile - a real solar-atlas-shaped profile, not an idealized Gaussian.
        var values = new double[(2 * maxShiftPixels) + 1];
        for (var i = 0; i < values.Length; i++)
        {
            var shift = i - maxShiftPixels;
            values[i] = realHAlphaWindow.IntensityAt(realHAlphaWindow.CenterWavelengthAngstroms + (shift * dispersion))!.Value;
        }

        var observed = new SpectralProfile(values, -maxShiftPixels);

        var identifier = new SpectralLineIdentifier(Instrument, PixelSizeMicrons); // real bundled resource
        var result = identifier.Identify(observed);

        Assert.Equal(SpectralRay.HAlpha, result.IdentifiedRay);
    }

    [Fact]
    public void Identify_NoReferenceWindowsSupplied_ReturnsNoConfidentMatch()
    {
        var observed = new SpectralProfile(Enumerable.Repeat(1000.0, 41).ToArray(), -20);
        var identifier = new SpectralLineIdentifier(Instrument, PixelSizeMicrons, referenceWindows: []);

        var result = identifier.Identify(observed);

        Assert.Null(result.IdentifiedRay);
        Assert.Empty(result.AllCandidates);
    }

    private static ReferenceWindow BuildSyntheticWindow(double centerWavelengthAngstroms, Func<double, double> shape)
    {
        const double halfWidth = 8.0;
        const double step = 0.01;
        var count = (int)System.Math.Round((2 * halfWidth / step) + 1);
        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = shape(-halfWidth + (i * step));
        }

        return new ReferenceWindow(centerWavelengthAngstroms, step, values);
    }

    /// <summary>An observed profile built from <paramref name="shape"/> at pixel-shift resolution via
    /// <paramref name="dispersionAngstromsPerPixel"/>, plus small deterministic noise.</summary>
    private static SpectralProfile BuildObservedProfile(double dispersionAngstromsPerPixel, int maxShiftPixels, Func<double, double> shape, double noise)
    {
        var random = new Random(1);
        var values = new double[(2 * maxShiftPixels) + 1];
        for (var i = 0; i < values.Length; i++)
        {
            var shift = i - maxShiftPixels;
            values[i] = shape(shift * dispersionAngstromsPerPixel) + (random.NextDouble() * noise * 2 - noise);
        }

        return new SpectralProfile(values, -maxShiftPixels);
    }
}
