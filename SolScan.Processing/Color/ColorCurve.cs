// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/color/ColorCurve.java
// and the ColorCurve-aware overload of .../sun/ImageUtils.java's convertToRGB (Apache License,
// Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE file for full
// attribution. The original precomputes and caches each channel's quadratic polynomial in a static
// ConcurrentHashMap keyed by (in, out) pairs, shared across every ColorCurve instance in the JVM; this
// port skips the cache entirely and just fits fresh each time a ColorCurve is constructed - reusing
// the general-purpose SolScan.Processing.Math.LinearRegression.SecondOrderRegression (already ported
// for spectral-line-curvature detection) rather than the original's own hand-derived polynomial solve,
// since a 3-point regression is cheap enough that per-instance caching would save nothing measurable.

using SolScan.Core.Processing;
using SolScan.Processing.Math;

namespace SolScan.Processing.Color;

/// <summary>A per-channel quadratic mono-to-RGB mapping built from <see cref="ColorCurveParams"/> -
/// each channel gets its own curve through <c>(0, 0)</c>, <c>(In &lt;&lt; 8, Out &lt;&lt; 8)</c> (the
/// 0-255-scale input shifted up into the 16-bit container range) and <c>(65535, 65535)</c>.</summary>
public sealed class ColorCurve
{
    private const double Max = 65535;

    private readonly QuadraticPolynomial _r;
    private readonly QuadraticPolynomial _g;
    private readonly QuadraticPolynomial _b;

    public ColorCurve(ColorCurveParams parameters)
    {
        _r = FitChannel(parameters.RIn, parameters.ROut);
        _g = FitChannel(parameters.GIn, parameters.GOut);
        _b = FitChannel(parameters.BIn, parameters.BOut);
    }

    private static QuadraticPolynomial FitChannel(int inValue, int outValue) =>
        LinearRegression.SecondOrderRegression(
        [
            new Point2D(0, 0),
            new Point2D(inValue << 8, outValue << 8),
            new Point2D(Max, Max),
        ]);

    /// <summary>Applies this curve to every pixel of <paramref name="mono"/> (values expected in
    /// <c>[0, 65535]</c>), producing three independent channel buffers - direct port of astro4j's
    /// <c>ImageUtils.convertToRGB(ColorCurve, float[][])</c>.</summary>
    public (float[,] R, float[,] G, float[,] B) Apply(float[,] mono)
    {
        var height = mono.GetLength(0);
        var width = mono.GetLength(1);
        var r = new float[height, width];
        var g = new float[height, width];
        var b = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = mono[y, x];
                r[y, x] = ClampToRange(_r.Evaluate(v));
                g[y, x] = ClampToRange(_g.Evaluate(v));
                b[y, x] = ClampToRange(_b.Evaluate(v));
            }
        }

        return (r, g, b);
    }

    private static float ClampToRange(double v) => (float)System.Math.Round(System.Math.Clamp(v, 0, Max));
}
