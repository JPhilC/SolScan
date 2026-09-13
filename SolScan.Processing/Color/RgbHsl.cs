// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/sun/ImageUtils.java's
// fromRGBtoHSL/fromHSLtoRGB (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0).
// See SolScan's NOTICE file for full attribution. The original parallelizes each conversion across row
// strips (RowStrips.forEach) - dropped here, matching this project's general stance of porting the
// per-pixel algorithm itself and leaving parallelization as a separate, not-yet-needed concern (these
// arrays are the size of one processed disk image, not a live-preview frame).

namespace SolScan.Processing.Color;

/// <summary>Whole-image RGB&lt;-&gt;HSL conversion (values on the 0-65535 container scale in, HSL
/// components normalized to <c>[0, 1]</c> for S/L and degrees for H) - the array-based counterpart to
/// <c>SolScan.Core.Processing.SpectralRay</c>'s own single-pixel RGB&lt;-&gt;HSL helpers, needed here
/// to stretch a colorized image's lightness channel without disturbing its hue/saturation (see
/// <see cref="Colorize"/>).</summary>
public static class RgbHsl
{
    public static (float[,] H, float[,] S, float[,] L) ToHsl(float[,] r, float[,] g, float[,] b)
    {
        var height = r.GetLength(0);
        var width = r.GetLength(1);
        var h = new float[height, width];
        var s = new float[height, width];
        var l = new float[height, width];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var rf = r[y, x] / 65535f;
                var gf = g[y, x] / 65535f;
                var bf = b[y, x] / 65535f;

                var max = System.Math.Max(rf, System.Math.Max(gf, bf));
                var min = System.Math.Min(rf, System.Math.Min(gf, bf));
                var delta = max - min;

                var hue = 0.0f;
                if (delta != 0)
                {
                    if (max == rf)
                    {
                        hue = (gf - bf) / delta % 6;
                    }
                    else if (max == gf)
                    {
                        hue = ((bf - rf) / delta) + 2;
                    }
                    else
                    {
                        hue = ((rf - gf) / delta) + 4;
                    }
                }

                hue *= 60.0f;
                if (hue < 0)
                {
                    hue += 360.0f;
                }

                var lightness = (max + min) / 2;
                var saturation = delta == 0 ? 0 : delta / (1 - System.Math.Abs((2 * lightness) - 1));
                if (lightness <= 0.0001f)
                {
                    saturation = 0;
                }

                h[y, x] = System.Math.Clamp(hue, 0, 360);
                s[y, x] = System.Math.Clamp(saturation, 0, 1.0f);
                l[y, x] = System.Math.Clamp(lightness, 0, 1.0f);
            }
        }

        return (h, s, l);
    }

    public static (float[,] R, float[,] G, float[,] B) FromHsl(float[,] h, float[,] s, float[,] l)
    {
        var height = h.GetLength(0);
        var width = h.GetLength(1);
        var r = new float[height, width];
        var g = new float[height, width];
        var b = new float[height, width];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var chroma = (1 - System.Math.Abs((2 * l[y, x]) - 1)) * s[y, x];
                var hueSegment = h[y, x] / 60.0f;
                var k = chroma * (1 - System.Math.Abs((hueSegment % 2) - 1));
                var m = l[y, x] - (chroma / 2);

                float rf, gf, bf;
                if (hueSegment < 1)
                {
                    (rf, gf, bf) = (chroma, k, 0);
                }
                else if (hueSegment < 2)
                {
                    (rf, gf, bf) = (k, chroma, 0);
                }
                else if (hueSegment < 3)
                {
                    (rf, gf, bf) = (0, chroma, k);
                }
                else if (hueSegment < 4)
                {
                    (rf, gf, bf) = (0, k, chroma);
                }
                else if (hueSegment < 5)
                {
                    (rf, gf, bf) = (k, 0, chroma);
                }
                else
                {
                    (rf, gf, bf) = (chroma, 0, k);
                }

                r[y, x] = (rf + m) * 65535f;
                g[y, x] = (gf + m) * 65535f;
                b[y, x] = (bf + m) * 65535f;
            }
        }

        return (r, g, b);
    }
}
