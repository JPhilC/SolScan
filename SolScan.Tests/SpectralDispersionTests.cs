using SolScan.Core.Equipment;
using SolScan.Core.Processing;

namespace SolScan.Tests;

public class SpectralDispersionTests
{
    [Fact]
    public void ComputeAngstromsPerPixel_SolEx_HAlpha_MatchesHandWorkedExample()
    {
        var solEx = SpectrographProfile.CreateSolEx(); // 34°, 2400 l/mm, order 1, 125mm camera focal length
        var pixelSizeMicrons = 2.0; // ASI678MM ballpark, matching SimulatedCameraDevice's own placeholder

        var angstromsPerPixel = SpectralDispersion.ComputeAngstromsPerPixel(solEx, SpectralRay.HAlpha.WavelengthAngstroms, pixelSizeMicrons);

        // Hand-worked: beta = asin(1*2400*6.56281e-4 / (2*cos(17°))) - 17° ≈ 38.42°, cos(beta) ≈ 0.7833,
        // dispersion = 0.002mm * 0.7833 * 1e7 / (2400*125) ≈ 0.0522 Å/px - a physically plausible
        // Sol'Ex-class figure (tens of mÅ/pixel), not an arbitrary number.
        Assert.InRange(angstromsPerPixel, 0.050, 0.055);
    }

    [Fact]
    public void ComputeAngstromsPerPixel_IsAlwaysPositive()
    {
        var solEx = SpectrographProfile.CreateSolEx();

        foreach (var ray in SpectralRay.Predefined)
        {
            if (ray.WavelengthAngstroms <= 0)
            {
                continue; // SpectralRay.Other - no wavelength to compute a dispersion for.
            }

            var dispersion = SpectralDispersion.ComputeAngstromsPerPixel(solEx, ray.WavelengthAngstroms, 2.0);
            Assert.True(dispersion > 0, $"{ray.Label} produced a non-positive dispersion: {dispersion}");
        }
    }

    [Fact]
    public void ComputeAngstromsPerPixel_ScalesLinearlyWithPixelSizeAndBinning()
    {
        var solEx = SpectrographProfile.CreateSolEx();
        var wavelength = SpectralRay.HAlpha.WavelengthAngstroms;

        var atOnePixel = SpectralDispersion.ComputeAngstromsPerPixel(solEx, wavelength, pixelSizeMicrons: 2.0, binning: 1);
        var atDoublePixelSize = SpectralDispersion.ComputeAngstromsPerPixel(solEx, wavelength, pixelSizeMicrons: 4.0, binning: 1);
        var atDoubleBinning = SpectralDispersion.ComputeAngstromsPerPixel(solEx, wavelength, pixelSizeMicrons: 2.0, binning: 2);

        // Both a doubled physical pixel size and a doubled bin factor double the effective sample size
        // on the sensor, so both should double the Å spanned by one (binned) pixel identically.
        Assert.InRange(atDoublePixelSize / atOnePixel, 1.999, 2.001);
        Assert.InRange(atDoubleBinning / atOnePixel, 1.999, 2.001);
    }

    [Fact]
    public void ComputeAngstromsPerPixel_LargerGratingDensity_GivesFinerDispersion()
    {
        var coarse = SpectrographProfile.CreateSolEx();
        // A modest +20% increase, not a doubling: the grating equation only has a real solution while
        // m*N*lambda/(2*cos(A/2)) stays within [-1, 1] (asin's domain) - doubling 2400 l/mm at this
        // instrument's 34° total angle pushes that ratio past 1, an unphysical combination this fixed-
        // geometry design simply couldn't build, not a bug in the formula.
        var fine = coarse with { GratingDensityLinesPerMm = (int)(coarse.GratingDensityLinesPerMm * 1.2) };
        var wavelength = SpectralRay.HAlpha.WavelengthAngstroms;

        var coarseDispersion = SpectralDispersion.ComputeAngstromsPerPixel(coarse, wavelength, 2.0);
        var fineDispersion = SpectralDispersion.ComputeAngstromsPerPixel(fine, wavelength, 2.0);

        // A denser grating spreads the same wavelength range over more pixels, so each pixel spans
        // fewer Å - finer (smaller) dispersion, not coarser.
        Assert.True(fineDispersion < coarseDispersion);
    }
}
