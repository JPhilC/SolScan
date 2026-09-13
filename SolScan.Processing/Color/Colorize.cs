// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/expr/impl/Colorize.java's
// own doColorize(width, height, mono, int[] rgb) overload (the wavelength-approximated-colour path -
// the ColorCurve-driven overload is ImageUtils.convertToRGB, ported as SolScan.Processing.Color.ColorCurve.Apply
// instead) (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's
// NOTICE file for full attribution. The final "stretch the whole RGB image" step is astro4j's own
// StretchingStrategy.stretch(RGBImage) default method (RGB -> HSL, stretch lightness only, HSL -> RGB)
// as invoked by `new LinearStrechingStrategy(0, 0.9f * MAX).stretch(rgbImage)` - ported here as
// StretchByLightness, a direct specialization for the one stretch strategy this pipeline actually
// drives through it (a plain min/max linear stretch), not a general RGBImage-stretch dispatch
// mechanism SolScan.Processing has no other use for yet.

using SolScan.Core.Processing;
using SolScan.Processing.Stretching;

namespace SolScan.Processing.Color;

/// <summary>Produces <see cref="GeneratedImageKind.Colorized"/>'s RGB pixel data from a stretched mono
/// image, via whichever of a ray's two colour sources applies - see <see cref="SpectralRay.ColorCurve"/>'s
/// own doc comment for which predefined rays use which.</summary>
public static class Colorize
{
    private const double MaxPixelValue = 65535;

    /// <summary>The <see cref="SpectralRay.ColorCurve"/> path (currently just <see cref="SpectralRay.HAlpha"/>) -
    /// each channel mapped independently through its own fitted curve.</summary>
    public static (float[,] R, float[,] G, float[,] B) WithCurve(float[,] mono, ColorCurveParams curveParams) =>
        new ColorCurve(curveParams).Apply(mono);

    /// <summary>The fallback path for every other predefined ray with a real wavelength (see
    /// <see cref="SpectralRay.ToRgb"/>): gamma-stretch a copy of the mono data, tint it by the given
    /// RGB triple, then stretch the whole result's lightness out toward white (leaving hue/saturation
    /// alone) so the final image isn't left dim.</summary>
    public static (float[,] R, float[,] G, float[,] B) WithWavelengthRgb(float[,] mono, (byte R, byte G, byte B) rgb)
    {
        var height = mono.GetLength(0);
        var width = mono.GetLength(1);
        var copy = (float[,])mono.Clone();
        GammaStrategy.Stretch(copy, 1.2, MaxPixelValue);

        var r = new float[height, width];
        var g = new float[height, width];
        var b = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var gray = copy[y, x];
                r[y, x] = gray * rgb.R / 255f;
                g[y, x] = gray * rgb.G / 255f;
                b[y, x] = gray * rgb.B / 255f;
            }
        }

        return StretchByLightness(r, g, b, 0, 0.9f * (float)MaxPixelValue);
    }

    private static (float[,] R, float[,] G, float[,] B) StretchByLightness(float[,] r, float[,] g, float[,] b, float lo, float hi)
    {
        var (h, s, l) = RgbHsl.ToHsl(r, g, b);
        var height = l.GetLength(0);
        var width = l.GetLength(1);

        var lightness = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                lightness[y, x] = l[y, x] * 65535f;
            }
        }

        LinearStretchStrategy.Stretch(lightness, lo, hi);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                l[y, x] = lightness[y, x] / 65535f;
            }
        }

        return RgbHsl.FromHsl(h, s, l);
    }
}
