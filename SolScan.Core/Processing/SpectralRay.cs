// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/params/SpectralRay.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>
/// A named spectral line SolScan can be told to study - ported from astro4j's <c>SpectralRay</c>
/// (the 12 predefined lines + "Other"). Wavelengths per
/// <see href="https://en.wikipedia.org/wiki/Fraunhofer_lines"/>, same source JSolex's own doc comment
/// cites. Now also carries the auto-colorization <see cref="ColorCurve"/> and <see cref="ToRgb"/> -
/// astro4j's own <c>colorCurve</c> field and <c>toRGB()</c>/<c>toSimpleRGB()</c> methods, needed once
/// <see cref="GeneratedImageKind.Colorized"/> became a real pipeline output. Still dropped:
/// <c>automaticScripts</c> (per-line ImageMath scripts - scripts generally are out of scope for now,
/// see SolScan CLAUDE.md).
/// </summary>
/// <param name="Label">Display label, e.g. "H-alpha".</param>
/// <param name="WavelengthAngstroms">Wavelength in Ångströms - 0 for <see cref="Other"/>, which has
/// no fixed line.</param>
/// <param name="IsEmission">True for an emission line (only Helium D3 among the predefined set).</param>
/// <param name="ColorCurve">A fixed colorization curve for this ray, or null to fall back to
/// <see cref="ToRgb"/>'s wavelength approximation - see <see cref="ColorCurveParams"/>'s own doc
/// comment for which predefined rays actually have one (currently just <see cref="HAlpha"/>).</param>
public sealed record SpectralRay(string Label, double WavelengthAngstroms, bool IsEmission, ColorCurveParams? ColorCurve = null)
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
    public static readonly SpectralRay HAlpha = new("H-alpha", 6562.81, false, ColorCurveParams.HAlpha);

    /// <summary>No fixed line - the user studies whatever the scan happens to be centred on. Has no
    /// wavelength to derive a colour from either, so <see cref="GeneratedImageKind.Colorized"/> simply
    /// isn't produced when this is the studied ray - matching astro4j's own silent no-op for this case
    /// (<c>ProcessingWorkflow.produceColorizedImage</c>'s <c>orElse</c> branch, reached only when
    /// <c>wavelength().nanos() &gt; 0</c>).</summary>
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

    /// <summary>Approximates the perceived colour of this ray's wavelength as a display RGB triple -
    /// direct port of astro4j's <c>SpectralRay.toRGB()</c>, used for <see cref="GeneratedImageKind.Colorized"/>
    /// on every predefined ray that has no fixed <see cref="ColorCurve"/> (i.e. every one but
    /// <see cref="HAlpha"/>). Meaningless for <see cref="Other"/> (wavelength 0) - callers must check
    /// <see cref="WavelengthAngstroms"/> is positive first, matching astro4j's own call site.</summary>
    public (byte R, byte G, byte B) ToRgb() => ImproveEsthetics(ToSimpleRgb());

    /// <summary>The underlying CIE-ish visible-spectrum approximation <see cref="ToRgb"/> then
    /// desaturates/lightens slightly for display - direct port of astro4j's own <c>toSimpleRGB()</c>,
    /// which works in nanometres (this record's own <see cref="WavelengthAngstroms"/> is Ångströms,
    /// hence the /10 conversion here).</summary>
    public (byte R, byte G, byte B) ToSimpleRgb()
    {
        var wavelength = WavelengthAngstroms / 10.0;
        double r, g, b;
        if (wavelength >= 380 && wavelength < 440)
        {
            r = -(wavelength - 440) / (440 - 380);
            g = 0.0;
            b = 1.0;
        }
        else if (wavelength >= 440 && wavelength < 490)
        {
            r = 0.0;
            g = (wavelength - 440) / (490 - 440);
            b = 1.0;
        }
        else if (wavelength >= 490 && wavelength < 510)
        {
            r = 0.0;
            g = 1.0;
            b = -(wavelength - 510) / (510 - 490);
        }
        else if (wavelength >= 510 && wavelength < 580)
        {
            r = (wavelength - 510) / (580 - 510);
            g = 1.0;
            b = 0.0;
        }
        else if (wavelength >= 580 && wavelength < 645)
        {
            r = 1.0;
            g = -(wavelength - 645) / (645 - 580);
            b = 0.0;
        }
        else if (wavelength >= 645 && wavelength < 781)
        {
            r = 1.0;
            g = 0.0;
            b = 0.0;
        }
        else
        {
            r = 0.0;
            g = 0.0;
            b = 0.0;
        }

        double factor;
        if (wavelength >= 380 && wavelength < 420)
        {
            factor = 0.3 + (0.7 * (wavelength - 380) / (420 - 380));
        }
        else if (wavelength >= 420 && wavelength < 701)
        {
            factor = 1.0;
        }
        else if (wavelength >= 701 && wavelength < 781)
        {
            factor = 0.3 + (0.7 * (780 - wavelength) / (780 - 700));
        }
        else
        {
            factor = 0.0;
        }

        byte rByte = r == 0.0 ? (byte)0 : (byte)System.Math.Round(255 * System.Math.Pow(r * factor, 0.7));
        byte gByte = g == 0.0 ? (byte)0 : (byte)System.Math.Round(255 * System.Math.Pow(g * factor, 0.7));
        byte bByte = b == 0.0 ? (byte)0 : (byte)System.Math.Round(255 * System.Math.Pow(b * factor, 0.7));
        return (rByte, gByte, bByte);
    }

    /// <summary>Desaturates by 15% and lightens by 45% of the remaining headroom to white - direct
    /// port of astro4j's own <c>improveEsthetics()</c>, via a single-pixel RGB&lt;-&gt;HSL round trip
    /// (distinct from, and much simpler than, the whole-image array conversion
    /// <c>SolScan.Processing.Color</c>'s own colorization pipeline needs).</summary>
    private static (byte R, byte G, byte B) ImproveEsthetics((byte R, byte G, byte B) rgb)
    {
        var (h, s, l) = RgbToHsl(rgb.R, rgb.G, rgb.B);
        s *= 0.85f;
        l += (1.0f - l) * 0.45f;
        return HslToRgb(h, s, l);
    }

    private static (float H, float S, float L) RgbToHsl(byte r, byte g, byte b)
    {
        var rf = r / 255.0f;
        var gf = g / 255.0f;
        var bf = b / 255.0f;
        var max = System.Math.Max(rf, System.Math.Max(gf, bf));
        var min = System.Math.Min(rf, System.Math.Min(gf, bf));
        float h, s;
        var l = (max + min) / 2.0f;

        if (max == min)
        {
            h = s = 0.0f;
        }
        else
        {
            var d = max - min;
            s = l > 0.5f ? d / (2.0f - max - min) : d / (max + min);
            if (max == rf)
            {
                h = ((gf - bf) / d) + (gf < bf ? 6.0f : 0.0f);
            }
            else if (max == gf)
            {
                h = ((bf - rf) / d) + 2.0f;
            }
            else
            {
                h = ((rf - gf) / d) + 4.0f;
            }

            h /= 6.0f;
        }

        return (h, s, l);
    }

    private static (byte R, byte G, byte B) HslToRgb(float h, float s, float l)
    {
        float r, g, b;
        if (s == 0.0f)
        {
            r = g = b = l;
        }
        else
        {
            var q = l < 0.5f ? l * (1.0f + s) : l + s - (l * s);
            var p = (2.0f * l) - q;
            r = HueToRgb(p, q, h + (1.0f / 3.0f));
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - (1.0f / 3.0f));
        }

        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static float HueToRgb(float p, float q, float t)
    {
        if (t < 0.0f) t += 1.0f;
        if (t > 1.0f) t -= 1.0f;
        if (t < 1.0f / 6.0f) return p + ((q - p) * 6.0f * t);
        if (t < 1.0f / 2.0f) return q;
        if (t < 2.0f / 3.0f) return p + ((q - p) * ((2.0f / 3.0f) - t) * 6.0f);
        return p;
    }
}
