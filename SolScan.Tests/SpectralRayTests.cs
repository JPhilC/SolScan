using SolScan.Core.Processing;

namespace SolScan.Tests;

public class SpectralRayTests
{
    [Fact]
    public void Predefined_HasThirteenEntries_TwelveLinesPlusOther()
    {
        Assert.Equal(13, SpectralRay.Predefined.Count);
    }

    [Fact]
    public void Predefined_OtherIsLast_WithZeroWavelength()
    {
        var last = SpectralRay.Predefined[^1];

        Assert.Equal(SpectralRay.Other, last);
        Assert.Equal(0, last.WavelengthAngstroms);
    }

    [Fact]
    public void Predefined_TwelveNamedLines_AreInAscendingWavelengthOrder()
    {
        var namedLines = SpectralRay.Predefined.Take(SpectralRay.Predefined.Count - 1).ToList();

        var sorted = namedLines.OrderBy(r => r.WavelengthAngstroms).ToList();
        Assert.Equal(sorted, namedLines);
    }

    [Fact]
    public void HAlpha_WavelengthMatchesKnownValue()
    {
        Assert.Equal(6562.81, SpectralRay.HAlpha.WavelengthAngstroms, precision: 2);
    }
}
