// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/stretching/ArcsinhStretchingStrategy.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution. Only the CPU path (stretchCPU) is ported - the original's OpenCL/GPU path
// is dropped throughout SolScan.Processing, same as GammaStrategy/ClaheStrategy's own header comments.
// The RGBImage overload (used elsewhere in astro4j to stretch a whole colour image's shared luminance)
// isn't ported either - SolScan's own colorized-image pipeline (SolScan.Processing.Color.Colorize)
// only ever calls this on a mono buffer, before colorizing it, not after. The original constructor's
// third `maxStretch` parameter is dropped too: confirmed dead in astro4j itself - stored on the
// instance and exposed via a getter, but never read by stretchCPU/stretchPixel/stretchGPU or by any
// caller anywhere in that codebase - matching this project's established "confirmed dead code isn't
// faithfully reproduced" precedent (see e.g. DiskEdgeDetector's own header comment).

namespace SolScan.Processing.Stretching;

/// <summary>Arcsinh ("asinh") stretch, as described in SIRIL's docs
/// (<see href="https://free-astro.org/siril_doc-en/co/AsinhTransformation.html"/>): pulls faint
/// detail up more aggressively than a straight linear stretch while compressing already-bright
/// pixels less, then renormalizes the whole result back out to the full 16-bit range via
/// <see cref="LinearStretchStrategy.StretchDefault"/>. Used by <see cref="SolScan.Processing.Color.Colorize"/>
/// as the first step in producing <c>GeneratedImageKind.Colorized</c>.</summary>
public sealed class ArcsinhStretchingStrategy
{
    private const double MaxPixelValue = 65535;

    private readonly double _stretch;
    private readonly double _asinhOfStretch;
    private readonly double _normalizedBlackPoint;

    public ArcsinhStretchingStrategy(float blackPoint, float stretch)
    {
        _stretch = stretch;
        _asinhOfStretch = System.Math.Asinh(stretch);
        _normalizedBlackPoint = blackPoint / MaxPixelValue;
    }

    public void Stretch(float[,] data)
    {
        var height = data.GetLength(0);
        var width = data.GetLength(1);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = StretchPixel(data[y, x]);
                if (float.IsNaN(v))
                {
                    v = 0;
                }

                data[y, x] = System.Math.Clamp(v, 0, (float)MaxPixelValue);
            }
        }

        LinearStretchStrategy.StretchDefault(data);
    }

    private float StretchPixel(float v)
    {
        if (v == 0)
        {
            return 0;
        }

        var original = v / MaxPixelValue;
        var pixel = System.Math.Max(0, original - _normalizedBlackPoint);
        var stretched = pixel * System.Math.Asinh(original * _stretch) / (original * _asinhOfStretch);
        return (float)(stretched * MaxPixelValue);
    }
}
