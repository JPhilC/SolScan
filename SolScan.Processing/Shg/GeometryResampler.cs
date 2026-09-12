// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/util/GeometryUtils.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

using SolScan.Processing.Math;

namespace SolScan.Processing.Shg;

/// <summary>Applies a <see cref="Math.GeometryTransform"/> (shear the rows to remove tilt, then scale
/// both axes to make the disk circular) to an image via a separable Catmull-Rom cubic resample -
/// smoother than nearest-neighbour/bilinear, and on a downscaled axis widens its own kernel to average
/// over the source footprint rather than aliasing. Also carries <see cref="ComputeCorrectedCircle"/>,
/// which re-expresses the detected ellipse in the corrected image's own coordinate system (an
/// analytic conic transform, not a re-fit) - <see cref="DiskGeometryCorrector"/>'s autocrop step needs
/// that, not the pre-correction ellipse's original coordinates.</summary>
public static class GeometryResampler
{
    private const double KernelRadius = 2;

    /// <summary><paramref name="transform"/> must have been computed for <paramref name="image"/>'s
    /// own dimensions - the shift it carries depends on the height, and the output dimensions derive
    /// from both.</summary>
    public static float[,] ApplyCorrection(float[,] image, Math.GeometryTransform transform, float blackPoint, double maxPixelValue)
    {
        var width = transform.Width;
        var height = transform.Height;
        if (image.GetLength(1) != width || image.GetLength(0) != height)
        {
            throw new ArgumentException(
                $"Geometry transform was computed for an image of {width}x{height} but the image is {image.GetLength(1)}x{image.GetLength(0)}.");
        }

        var shear = transform.Shear;
        var shift = transform.Shift;
        var sx = transform.Sx;
        var sy = transform.Sy;
        var offsetX = transform.OffsetX;
        var offsetY = transform.OffsetY;

        var extendedWidth = transform.ExtendedWidth;
        var newWidth = transform.OutputWidth;
        var newHeight = transform.OutputHeight;
        var output = new float[newHeight, newWidth];

        // On a downscale, one output pixel covers several input ones: widen the kernel to the source
        // footprint so the redundant samples are averaged instead of dropped.
        var xSupport = sx < 1 ? 1 / sx : 1;
        var ySupport = sy < 1 ? 1 / sy : 1;

        for (var y = 0; y < newHeight; y++)
        {
            var v = (y - offsetY) / sy;
            if (v < 0 || v > height - 1)
            {
                for (var x = 0; x < newWidth; x++)
                {
                    output[y, x] = blackPoint;
                }

                continue;
            }

            var sheared = shift - (v * shear);
            for (var x = 0; x < newWidth; x++)
            {
                var u = (x - offsetX) / sx;
                output[y, x] = u < 0 || u > extendedWidth - 1
                    ? blackPoint
                    : SampleCatmullRom(image, u + sheared, v, width, height, xSupport, ySupport, maxPixelValue);
            }
        }

        return output;
    }

    /// <summary>Computes the corrected ellipse analytically, by applying to the conic the very
    /// transformation the warp applies to the pixels, instead of re-fitting one against the corrected
    /// image (which is - by construction - close to circular, but re-fitting would waste the disk-edge
    /// fit already done and could disagree with it slightly).</summary>
    public static Ellipse ComputeCorrectedCircle(Ellipse ellipse, Math.GeometryTransform transform)
    {
        var conic = new Matrix3x3(
            ellipse.A, ellipse.B / 2, ellipse.D / 2,
            ellipse.B / 2, ellipse.C, ellipse.E / 2,
            ellipse.D / 2, ellipse.E / 2, ellipse.F);

        var shear = transform.Shear;
        var sx = transform.Sx;
        var sy = transform.Sy;
        var tx = transform.OffsetX - (sx * transform.Shift);
        var ty = transform.OffsetY;

        // source coordinates as a function of the corrected ones, in homogeneous form
        var inverse = new Matrix3x3(
            1 / sx, -shear / sy, (shear * ty / sy) - (tx / sx),
            0, 1 / sy, -ty / sy,
            0, 0, 1);

        var transformed = inverse.Transpose() * (conic * inverse);
        return new Ellipse(
            transformed.M00,
            2 * transformed.M01,
            transformed.M11,
            2 * transformed.M02,
            2 * transformed.M12,
            transformed.M22);
    }

    private static float SampleCatmullRom(float[,] data, double x, double y, int width, int height, double xSupport, double ySupport, double maxPixelValue)
    {
        double value;
        if (ySupport == 1)
        {
            var j = (int)System.Math.Floor(y);
            var t = y - j;
            if (t == 0)
            {
                value = SampleRow(data, Clamp(j, height), x, width, xSupport);
            }
            else
            {
                var weights = CubicWeights.Of(t);
                value = weights.Apply(
                    SampleRow(data, Clamp(j - 1, height), x, width, xSupport),
                    SampleRow(data, Clamp(j, height), x, width, xSupport),
                    SampleRow(data, Clamp(j + 1, height), x, width, xSupport),
                    SampleRow(data, Clamp(j + 2, height), x, width, xSupport));
            }
        }
        else
        {
            double sum = 0;
            double weightSum = 0;
            var from = (int)System.Math.Ceiling(y - (KernelRadius * ySupport));
            var to = (int)System.Math.Floor(y + (KernelRadius * ySupport));
            for (var j = from; j <= to; j++)
            {
                var w = CatmullRom((j - y) / ySupport);
                if (w != 0)
                {
                    sum += w * SampleRow(data, Clamp(j, height), x, width, xSupport);
                    weightSum += w;
                }
            }

            value = weightSum == 0 ? 0 : sum / weightSum;
        }

        if (value < 0)
        {
            return 0;
        }

        return value > maxPixelValue ? (float)maxPixelValue : (float)value;
    }

    private static double SampleRow(float[,] data, int row, double x, int width, double support)
    {
        if (support == 1)
        {
            var i = (int)System.Math.Floor(x);
            var weights = CubicWeights.Of(x - i);
            return weights.Apply(
                data[row, Clamp(i - 1, width)],
                data[row, Clamp(i, width)],
                data[row, Clamp(i + 1, width)],
                data[row, Clamp(i + 2, width)]);
        }

        double sum = 0;
        double weightSum = 0;
        var from = (int)System.Math.Ceiling(x - (KernelRadius * support));
        var to = (int)System.Math.Floor(x + (KernelRadius * support));
        for (var i = from; i <= to; i++)
        {
            var w = CatmullRom((i - x) / support);
            if (w != 0)
            {
                sum += w * data[row, Clamp(i, width)];
                weightSum += w;
            }
        }

        return weightSum == 0 ? 0 : sum / weightSum;
    }

    private static double CatmullRom(double t)
    {
        var abs = System.Math.Abs(t);
        if (abs < 1)
        {
            return (((1.5 * abs) - 2.5) * abs * abs) + 1;
        }

        if (abs < KernelRadius)
        {
            return ((((-0.5 * abs) + 2.5) * abs) - 4) * abs + 2;
        }

        return 0;
    }

    private static int Clamp(int index, int length)
    {
        if (index < 0)
        {
            return 0;
        }

        return index >= length ? length - 1 : index;
    }

    private readonly record struct CubicWeights(double WMinus1, double W0, double W1, double W2)
    {
        public static CubicWeights Of(double t) => new(
            CatmullRom(t + 1), CatmullRom(t), CatmullRom(1 - t), CatmullRom(2 - t));

        public double Apply(double vMinus1, double v0, double v1, double v2) =>
            (WMinus1 * vMinus1) + (W0 * v0) + (W1 * v1) + (W2 * v2);
    }
}
