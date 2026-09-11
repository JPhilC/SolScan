// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/params/SpectralRay.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>
/// A named spectral line SolScan can be told to study - ported from astro4j's <c>SpectralRay</c>
/// (the 12 predefined lines + "Other"). Wavelengths per
/// <see href="https://en.wikipedia.org/wiki/Fraunhofer_lines"/>, same source JSolex's own doc comment
/// cites. Dropped from the original: the auto-colorization <c>colorCurve</c> (only H-alpha has one in
/// JSolex, and colorized output is out of scope until a real pipeline exists) and
/// <c>automaticScripts</c> (per-line ImageMath scripts - scripts generally are out of scope for now,
/// see SolScan CLAUDE.md).
/// </summary>
/// <param name="Label">Display label, e.g. "H-alpha".</param>
/// <param name="WavelengthAngstroms">Wavelength in Ångströms - 0 for <see cref="Other"/>, which has
/// no fixed line.</param>
/// <param name="IsEmission">True for an emission line (only Helium D3 among the predefined set).</param>
public sealed record SpectralRay(string Label, double WavelengthAngstroms, bool IsEmission)
{
    public static readonly SpectralRay CalciumK = new("Calcium (K)", 3933.66, false);
    public static readonly SpectralRay CalciumH = new("Calcium (H)", 3968.47, false);
    public static readonly SpectralRay CalciumIronG = new("Calcium+Iron+CH (G)", 4307.82, false);
    public static readonly SpectralRay HBeta = new("H-beta", 4861.34, false);
    public static readonly SpectralRay MagnesiumB1 = new("Magnesium (b1)", 5183.62, false);
    public static readonly SpectralRay IronE2 = new("Iron (E2)", 5270.39, false);
    public static readonly SpectralRay IronFe1_5302 = new("Iron (Fe I 5302)", 5302.29, false);
    public static readonly SpectralRay HeliumD3 = new("Helium (D3)", 5875.62, true);
    public static readonly SpectralRay IronFe1 = new("Iron (Fe I)", 5883.8166, false);
    public static readonly SpectralRay SodiumD2 = new("Sodium (D2)", 5889.95, false);
    public static readonly SpectralRay SodiumD1 = new("Sodium (D1)", 5895.92, false);
    public static readonly SpectralRay HAlpha = new("H-alpha", 6562.81, false);

    /// <summary>No fixed line - the user studies whatever the scan happens to be centred on.</summary>
    public static readonly SpectralRay Other = new("Other", 0, false);

    /// <summary>The 12 named lines sorted by wavelength ascending, with <see cref="Other"/> appended
    /// last rather than sorted in - matches astro4j's own <c>SpectralRay.predefined()</c>, which
    /// concatenates <see cref="Other"/> onto the end of the sorted stream rather than including its
    /// placeholder 0Å wavelength in the sort (where it would otherwise sort first).</summary>
    public static IReadOnlyList<SpectralRay> Predefined { get; } =
    [
        CalciumK, CalciumH, CalciumIronG, HBeta, MagnesiumB1, IronE2, IronFe1_5302,
        HeliumD3, IronFe1, SodiumD2, SodiumD1, HAlpha,
        // ^ already listed in ascending-wavelength order above; Other is appended, not sorted in.
        Other,
    ];
}
