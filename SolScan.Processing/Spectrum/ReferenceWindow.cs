namespace SolScan.Processing.Spectrum;

/// <summary>
/// A narrow slice of the solar reference-flux atlas around one named line's wavelength - what
/// <see cref="SpectralLineIdentifier"/> correlates an observed <see cref="SpectralProfile"/> against
/// for each candidate <see cref="SolScan.Core.Processing.SpectralRay"/>. See
/// <see cref="ReferenceWindowResource"/> for where these come from (a small bundled resource, itself
/// derived offline from the public BASS2000 atlas - not the atlas itself).
/// </summary>
/// <param name="CenterWavelengthAngstroms">The named line's own catalog wavelength - the window's
/// own centre.</param>
/// <param name="StepAngstroms">Uniform Å spacing between consecutive <see cref="Intensities"/> entries.</param>
/// <param name="Intensities">Reference solar-flux intensities across the window, low-to-high
/// wavelength, on a 0-9999 scale (the source atlas's own native scale) - only ever used for relative
/// (correlation) comparison, so the absolute scale doesn't matter on its own.</param>
public sealed record ReferenceWindow(double CenterWavelengthAngstroms, double StepAngstroms, IReadOnlyList<double> Intensities)
{
    public double MinWavelengthAngstroms => CenterWavelengthAngstroms - (StepAngstroms * (Intensities.Count - 1) / 2.0);
    public double MaxWavelengthAngstroms => CenterWavelengthAngstroms + (StepAngstroms * (Intensities.Count - 1) / 2.0);

    /// <summary>Linearly interpolated intensity at an arbitrary wavelength - null outside the
    /// window's own [<see cref="MinWavelengthAngstroms"/>, <see cref="MaxWavelengthAngstroms"/>] range.</summary>
    public double? IntensityAt(double wavelengthAngstroms)
    {
        if (wavelengthAngstroms < MinWavelengthAngstroms || wavelengthAngstroms > MaxWavelengthAngstroms)
        {
            return null;
        }

        var exactIndex = (wavelengthAngstroms - MinWavelengthAngstroms) / StepAngstroms;
        var lowerIndex = (int)System.Math.Floor(exactIndex);
        var upperIndex = System.Math.Min(lowerIndex + 1, Intensities.Count - 1);
        var frac = exactIndex - lowerIndex;
        return ((1 - frac) * Intensities[lowerIndex]) + (frac * Intensities[upperIndex]);
    }
}
