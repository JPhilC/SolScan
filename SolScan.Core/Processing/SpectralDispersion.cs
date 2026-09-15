using SolScan.Core.Equipment;

namespace SolScan.Core.Processing;

/// <summary>
/// Å-per-pixel dispersion at a given wavelength, from a <see cref="SpectrographProfile"/>'s own
/// optics - independently derived from the diffraction grating equation, not ported from astro4j's
/// <c>SpectrumAnalyzer.computeSpectralDispersion</c> (see <see cref="SolScan.Processing.Spectrum.SpectralLineIdentifier"/>'s
/// own doc comment for why: this is standard grating-spectrometer physics, published in Christian
/// Buil's own Sol'Ex documentation among other places, not really astro4j's own IP - re-deriving it
/// from the grating equation costs little and keeps the "original design, not a port" line real).
///
/// Model: a "constant deviation" spectrograph - light hits the grating at incidence angle α and
/// leaves at diffraction angle β (both measured from the grating normal, same side), with the fixed
/// mechanical angle between the collimator and camera optical axes - <see cref="SpectrographProfile.TotalAngleDegrees"/>,
/// A - equal to α − β. For a chosen centre wavelength λ, the grating equation mNλ = sin α + sin β
/// (order m, groove density N per mm) combined with α − β = A gives, via the sum-to-product identity
/// sin α + sin β = 2 sin(β + A/2) cos(A/2):
///
///   β = asin(mNλ / (2 cos(A/2))) − A/2
///
/// The usual paraxial grating-spectrometer relation (focal-plane position x ≈ f·β, so dλ/dx =
/// cos(β)/(mNf)) then gives the dispersion at that β. Cross-checked (not copied) against astro4j's
/// own published formula by working the algebra through for order 1: the two agree exactly, which is
/// the expected outcome for two independent derivations of the same standard physics, not evidence
/// either was copied from the other.
/// </summary>
public static class SpectralDispersion
{
    /// <param name="instrument">The SHG's own optics.</param>
    /// <param name="wavelengthAngstroms">The centre wavelength to compute dispersion at - dispersion
    /// varies (slightly) with wavelength, since β does, so this should be the actual line being
    /// studied, not an arbitrary reference point.</param>
    /// <param name="pixelSizeMicrons">The camera sensor's physical pixel size.</param>
    /// <param name="binning">Pixel binning in effect (1 = none) - each bin combines this many physical
    /// pixels into one larger effective sample, so the dispersion per *binned* pixel scales with it.</param>
    /// <returns>Å per (binned) pixel - always positive.</returns>
    public static double ComputeAngstromsPerPixel(
        SpectrographProfile instrument,
        double wavelengthAngstroms,
        double pixelSizeMicrons,
        int binning = 1)
    {
        var wavelengthMm = wavelengthAngstroms * 1e-7; // 1 Å = 1e-7 mm
        var order = instrument.DiffractionOrder;
        var density = instrument.GratingDensityLinesPerMm;
        var halfAngle = instrument.TotalAngleRadians / 2.0;

        var asinArgument = order * density * wavelengthMm / (2.0 * System.Math.Cos(halfAngle));
        if (asinArgument is < -1.0 or > 1.0)
        {
            // asin's domain is [-1, 1] - outside it, the grating equation has no real solution at all:
            // this instrument's fixed total angle simply cannot diffract this wavelength at this order/
            // density combination. System.Math.Asin would otherwise silently return NaN, which could
            // poison a correlation score downstream without ever surfacing as an obvious error.
            throw new ArgumentOutOfRangeException(nameof(wavelengthAngstroms), wavelengthAngstroms,
                $"No real diffraction angle exists for {instrument.Label} (order {order}, {density} l/mm, "
                    + $"{instrument.TotalAngleDegrees}° total angle) at {wavelengthAngstroms}Å.");
        }

        var beta = System.Math.Asin(asinArgument) - halfAngle;

        var pixelSizeMm = pixelSizeMicrons / 1000.0 * binning;
        var angstromsPerMm = 1e7; // 1 mm = 1e7 Å
        return pixelSizeMm * System.Math.Cos(beta) * angstromsPerMm / (order * density * instrument.CameraFocalLengthMm);
    }
}
