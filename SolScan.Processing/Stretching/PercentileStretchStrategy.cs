// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/stretching/PercentileStretchStrategy.java
// and its private helper .../stretching/PixelCollection.java (Apache License, Version 2.0:
// http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE file for full attribution. The
// pixel mask parameter (selecting which pixels feed the percentile computation, independent of which
// pixels the stretch itself is applied to) is ported since it costs little and matches the original's
// full public API; SolScan's own only current caller (SolScan.Processing.Color.Colorize) doesn't use it.

namespace SolScan.Processing.Stretching;

/// <summary>Maps a low/high percentile of the image's own pixel-value distribution to black/white
/// (or a scaled range, depending on <see cref="ClipMode"/>) - unlike <see cref="LinearStretchStrategy"/>'s
/// exact min/max, this is robust to a handful of outlier pixels at either extreme.</summary>
public sealed class PercentileStretchStrategy
{
    /// <summary>How pixels outside the percentile range are mapped.</summary>
    public enum ClipMode
    {
        /// <summary>Pure affine mapping (no clamping): pixels outside the percentile range may end up
        /// outside the displayable range.</summary>
        None,

        /// <summary>Pixels below the low percentile become black, pixels above the high percentile
        /// become white.</summary>
        Clamp,

        /// <summary>The white point is extended to the image's brightest pixel, so pixels brighter
        /// than the high percentile aren't clipped.</summary>
        Extend,
    }

    private const float MaxPixelValue = 65535f;

    private readonly double _lowPercentile;
    private readonly double _highPercentile;
    private readonly Func<int, int, bool>? _pixelMask;
    private readonly ClipMode _clipMode;

    public PercentileStretchStrategy(double lowPercentile, double highPercentile, Func<int, int, bool>? pixelMask = null, ClipMode clipMode = ClipMode.Clamp)
    {
        if (lowPercentile is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(lowPercentile), lowPercentile, "Low percentile must be in range [0, 100].");
        }

        if (highPercentile is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(highPercentile), highPercentile, "High percentile must be in range [0, 100].");
        }

        if (lowPercentile >= highPercentile)
        {
            throw new ArgumentOutOfRangeException(nameof(lowPercentile), lowPercentile, "Low percentile must be less than high percentile.");
        }

        _lowPercentile = lowPercentile;
        _highPercentile = highPercentile;
        _pixelMask = pixelMask;
        _clipMode = clipMode;
    }

    public void Stretch(float[,] data)
    {
        var height = data.GetLength(0);
        var width = data.GetLength(1);
        if (width == 0 || height == 0)
        {
            return;
        }

        var pixels = CollectPixels(data, width, height);
        if (pixels.Length == 0)
        {
            return;
        }

        Array.Sort(pixels);
        var lowValue = PercentileValue(pixels, _lowPercentile);
        var highValue = PercentileValue(pixels, _highPercentile);
        if (_clipMode == ClipMode.Extend)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    highValue = System.Math.Max(highValue, data[y, x]);
                }
            }
        }

        if (highValue <= lowValue)
        {
            return;
        }

        var range = highValue - lowValue;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = data[y, x];
                if (_clipMode != ClipMode.None && v <= lowValue)
                {
                    data[y, x] = 0;
                }
                else if (_clipMode != ClipMode.None && v >= highValue)
                {
                    data[y, x] = MaxPixelValue;
                }
                else
                {
                    data[y, x] = (v - lowValue) / range * MaxPixelValue;
                }
            }
        }
    }

    private float[] CollectPixels(float[,] data, int width, int height)
    {
        if (_pixelMask is null)
        {
            var all = new float[width * height];
            var index = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    all[index++] = data[y, x];
                }
            }

            return all;
        }

        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (_pixelMask(x, y))
                {
                    count++;
                }
            }
        }

        var selected = new float[count];
        var i = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (_pixelMask(x, y))
                {
                    selected[i++] = data[y, x];
                }
            }
        }

        return selected;
    }

    private static float PercentileValue(float[] sorted, double percentile) =>
        sorted[(int)System.Math.Round(percentile / 100.0 * (sorted.Length - 1))];
}
