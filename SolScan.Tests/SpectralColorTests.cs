using SolScan.Core.Processing;

namespace SolScan.Tests;

public class SpectralColorTests
{
    [Fact]
    public void ToRgb_MatchesSpectralRayToRgb_ForAllPredefinedWavelengths()
    {
        // The extraction (SpectralRay.ToRgb now delegates here) must be byte-for-byte identical to
        // what SpectralRayTests/ColorizeTests already exercise via the ray's own instance method.
        foreach (var ray in SpectralRay.Predefined)
        {
            Assert.Equal(ray.ToRgb(), SpectralColor.ToRgb(ray.WavelengthAngstroms));
            Assert.Equal(ray.ToSimpleRgb(), SpectralColor.ToSimpleRgb(ray.WavelengthAngstroms));
        }
    }

    [Theory]
    [InlineData(4500.0)] // blue-ish, between Ca H/K and H-beta - no predefined ray sits here
    [InlineData(5500.0)] // green-ish, between Fe(E2) and He(D3) - no predefined ray sits here
    [InlineData(6200.0)] // orange-ish, between Na D1 and H-alpha - no predefined ray sits here
    public void ToRgb_ArbitraryWavelength_ProducesAValidNonBlackColour(double wavelengthAngstroms)
    {
        var (r, g, b) = SpectralColor.ToRgb(wavelengthAngstroms);

        Assert.True(r > 0 || g > 0 || b > 0, $"Expected a non-black colour for {wavelengthAngstroms}Å, got ({r},{g},{b}).");
    }

    [Fact]
    public void ToRgb_ShiftingTowardRed_ShiftsHueTowardRed()
    {
        // A wavelength well into the blue end should read more blue-dominant than one well into the
        // red end - a basic sanity check that the mapping actually varies with wavelength in the
        // expected direction, independent of the exact per-band constants.
        var (blueR, _, blueB) = SpectralColor.ToRgb(4400);
        var (redR, _, redB) = SpectralColor.ToRgb(6800);

        Assert.True(blueB > blueR, "Expected the ~4400Å sample to read blue-dominant.");
        Assert.True(redR > redB, "Expected the ~6800Å sample to read red-dominant.");
    }
}
