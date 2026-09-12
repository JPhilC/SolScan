// Adapted from astro4j's math/src/main/java/me/champeau/a4j/math/image/ImageMath.java's `convolve`
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution. Trimmed to the one generic entry point <see cref="DiskEdgeDetector"/>
// needs - astro4j's own `ImageMath` also carries separable-Gaussian/Sobel/Laplacian fast paths not
// used by that port.

namespace SolScan.Processing.Shg;

/// <summary>A single edge-clamped 2D convolution, used by <see cref="DiskEdgeDetector"/>'s
/// blur-before-edge-detection step.</summary>
public static class ImageConvolution
{
    /// <summary>Convolves <paramref name="source"/> with <paramref name="kernel"/> (its own
    /// <paramref name="factor"/> normalizer applied after summing), replicating edge pixels for
    /// out-of-bounds taps and clamping the result to <c>[0, maxPixelValue]</c>.</summary>
    public static float[,] Convolve(float[,] source, float[,] kernel, float factor, double maxPixelValue)
    {
        var height = source.GetLength(0);
        var width = source.GetLength(1);
        var kHeight = kernel.GetLength(0);
        var kWidth = kernel.GetLength(1);
        var kcx = kWidth / 2;
        var kcy = kHeight / 2;
        var output = new float[height, width];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                float sum = 0;
                for (var ky = 0; ky < kHeight; ky++)
                {
                    var sy = System.Math.Clamp(y + ky - kcy, 0, height - 1);
                    for (var kx = 0; kx < kWidth; kx++)
                    {
                        var sx = System.Math.Clamp(x + kx - kcx, 0, width - 1);
                        sum += kernel[ky, kx] * source[sy, sx];
                    }
                }

                output[y, x] = (float)System.Math.Clamp(sum * factor, 0, maxPixelValue);
            }
        }

        return output;
    }

    /// <summary>A uniform (box) blur kernel of size <paramref name="n"/>x<paramref name="n"/>, with
    /// its own <c>1/(n*n)</c> normalizing factor - astro4j's <c>BlurKernel</c>.</summary>
    public static (float[,] Kernel, float Factor) BoxKernel(int n)
    {
        var kernel = new float[n, n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                kernel[y, x] = 1;
            }
        }

        return (kernel, 1f / (n * n));
    }

    /// <summary>The standard 3x3 Gaussian blur kernel - astro4j's <c>Kernel33.GAUSSIAN_BLUR</c>.</summary>
    public static readonly float[,] GaussianBlur3X3 = { { 1, 2, 1 }, { 2, 4, 2 }, { 1, 2, 1 } };

    public const float GaussianBlur3X3Factor = 1f / 16f;
}
