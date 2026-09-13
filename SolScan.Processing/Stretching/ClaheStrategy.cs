// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/stretching/ClaheStrategy.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution. Method-for-method port, including its own local per-tile
// histogram/CDF bookkeeping - deliberately not built on SolScan.Processing.Stretching.Histogram, since
// this type's own bin-index formula (divides by bins-1, matching the original) differs from that
// type's (divides by bins) - the same slight inconsistency exists between the two in astro4j itself.
//
// Contrast Limited Adaptive Histogram Equalization: partitions the image into tileSize x tileSize
// tiles, equalizes each tile's own (clipped) histogram, then bilinearly interpolates between
// neighbouring tiles' cumulative distributions so tile boundaries don't show as visible seams.

namespace SolScan.Processing.Stretching;

public sealed class ClaheStrategy
{
    public const int DefaultTileSize = 8;
    public const int DefaultBins = 64;
    public const double DefaultClip = 1.0;
    private const double MaxPixelValue = 65535;

    private readonly int _tileSize;
    private readonly int _bins;
    private readonly double _clipRatio;

    public ClaheStrategy(int tileSize, int bins, double clipRatio)
    {
        _tileSize = tileSize;
        _bins = bins;
        _clipRatio = clipRatio;
    }

    public void Stretch(float[,] data)
    {
        RangeExpansionStrategy.Stretch(data, MaxPixelValue);
        var height = data.GetLength(0);
        var width = data.GetLength(1);
        var xTilesCount = (int)System.Math.Ceiling(width / (double)_tileSize);
        var yTilesCount = (int)System.Math.Ceiling(height / (double)_tileSize);
        var cdf = new float[yTilesCount, xTilesCount][];
        for (var ty = 0; ty < yTilesCount; ty++)
        {
            for (var tx = 0; tx < xTilesCount; tx++)
            {
                var histogram = BuildTileHistogram(data, width, height, tx, ty);
                ClipHistogram(histogram);
                cdf[ty, tx] = ComputeCdf(histogram);
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var bin = FindHistogramBin(data[y, x]);
                var tileX = x / _tileSize;
                var tileY = y / _tileSize;
                var thisCdf = cdf[tileY, tileX][bin];
                float interpolated;
                if (IsCornerPixel(x, y, width, height))
                {
                    interpolated = thisCdf;
                }
                else if (IsLeftOrRightBorder(x, width))
                {
                    var distanceToTileCenter = y - CenterOf(tileY);
                    interpolated = distanceToTileCenter <= 0
                        ? LinearInterpolation(cdf[tileY - 1, tileX][bin], thisCdf, CenterOf(tileY - 1), CenterOf(tileY), y)
                        : LinearInterpolation(thisCdf, cdf[tileY + 1, tileX][bin], CenterOf(tileY), CenterOf(tileY + 1), y);
                }
                else if (IsTopOrBottomBorder(y, height))
                {
                    var distanceToTileCenter = x - CenterOf(tileX);
                    interpolated = distanceToTileCenter <= 0
                        ? LinearInterpolation(cdf[tileY, tileX - 1][bin], thisCdf, CenterOf(tileX - 1), CenterOf(tileX), x)
                        : LinearInterpolation(thisCdf, cdf[tileY, tileX + 1][bin], CenterOf(tileX), CenterOf(tileX + 1), x);
                }
                else
                {
                    float x1, y1;
                    float v11, v12, v21, v22;
                    var distanceToTileCenterX = x - CenterOf(tileX);
                    var distanceToTileCenterY = y - CenterOf(tileY);
                    if (distanceToTileCenterX <= 0)
                    {
                        if (distanceToTileCenterY <= 0)
                        {
                            x1 = CenterOf(tileX - 1);
                            y1 = CenterOf(tileY - 1);
                            v11 = cdf[tileY - 1, tileX - 1][bin];
                            v12 = cdf[tileY, tileX - 1][bin];
                            v21 = cdf[tileY - 1, tileX][bin];
                            v22 = cdf[tileY, tileX][bin];
                        }
                        else
                        {
                            x1 = CenterOf(tileX - 1);
                            y1 = CenterOf(tileY);
                            v11 = cdf[tileY, tileX - 1][bin];
                            v12 = cdf[tileY + 1, tileX - 1][bin];
                            v21 = cdf[tileY, tileX][bin];
                            v22 = cdf[tileY + 1, tileX][bin];
                        }
                    }
                    else
                    {
                        if (distanceToTileCenterY <= 0)
                        {
                            x1 = CenterOf(tileX);
                            y1 = CenterOf(tileY - 1);
                            v11 = cdf[tileY - 1, tileX][bin];
                            v12 = cdf[tileY, tileX][bin];
                            v21 = cdf[tileY - 1, tileX + 1][bin];
                            v22 = cdf[tileY, tileX + 1][bin];
                        }
                        else
                        {
                            x1 = CenterOf(tileX);
                            y1 = CenterOf(tileY);
                            v11 = cdf[tileY, tileX][bin];
                            v12 = cdf[tileY + 1, tileX][bin];
                            v21 = cdf[tileY, tileX + 1][bin];
                            v22 = cdf[tileY + 1, tileX + 1][bin];
                        }
                    }

                    var x2 = x1 + _tileSize;
                    var y2 = y1 + _tileSize;
                    interpolated = BilinearInterpolation(v11, v12, v21, v22, x1, y1, x2, y2, x, y);
                }

                data[y, x] = (float)(MaxPixelValue * interpolated);
            }
        }
    }

    private float CenterOf(int tile) => (tile * _tileSize) + (_tileSize / 2f);

    private bool IsLeftOrRightBorder(int x, int width)
    {
        var halfSize = _tileSize / 2;
        return x <= halfSize || x >= width - 1 - halfSize;
    }

    private bool IsTopOrBottomBorder(int y, int height)
    {
        var halfSize = _tileSize / 2;
        return y <= halfSize || y >= height - 1 - halfSize;
    }

    private bool IsCornerPixel(int x, int y, int width, int height) =>
        IsLeftOrRightBorder(x, width) && IsTopOrBottomBorder(y, height);

    private void ClipHistogram(int[] values)
    {
        var pixelCount = 0;
        foreach (var v in values)
        {
            pixelCount += v;
        }

        var clipLimit = (int)(_clipRatio * pixelCount / _bins);
        var excess = 0;
        for (var i = 0; i < _bins; i++)
        {
            if (values[i] > clipLimit)
            {
                excess += values[i] - clipLimit;
                values[i] = clipLimit;
            }
        }

        var increment = excess / _bins;
        var remainder = excess % _bins;
        for (var i = 0; i < _bins; i++)
        {
            values[i] += increment;
        }

        for (var i = 0; i < remainder; i++)
        {
            values[i]++;
        }
    }

    private static float[] ComputeCdf(int[] values)
    {
        var cumulative = new float[values.Length];
        cumulative[0] = values[0];
        for (var i = 1; i < values.Length; i++)
        {
            cumulative[i] = cumulative[i - 1] + values[i];
        }

        var max = cumulative[^1];
        if (max == 0)
        {
            for (var i = 0; i < values.Length; i++)
            {
                cumulative[i] = 1.0f;
            }

            return cumulative;
        }

        for (var i = 0; i < values.Length; i++)
        {
            cumulative[i] /= max;
        }

        return cumulative;
    }

    private int[] BuildTileHistogram(float[,] data, int width, int height, int tileX, int tileY)
    {
        var histogram = new int[_bins];
        var xStart = tileX * _tileSize;
        var xEnd = System.Math.Min(width, (tileX + 1) * _tileSize);
        var yStart = tileY * _tileSize;
        var yEnd = System.Math.Min(height, (tileY + 1) * _tileSize);
        for (var y = yStart; y < yEnd; y++)
        {
            for (var x = xStart; x < xEnd; x++)
            {
                histogram[FindHistogramBin(data[y, x])]++;
            }
        }

        return histogram;
    }

    private int FindHistogramBin(float src)
    {
        var bin = (int)System.Math.Round(src * (_bins - 1) / MaxPixelValue);
        return System.Math.Clamp(bin, 0, _bins - 1);
    }

    private static float LinearInterpolation(float v1, float v2, float x1, float x2, float a)
    {
        var slope = (a - x1) / (x2 - x1);
        return v1 + (slope * (v2 - v1));
    }

    private static float BilinearInterpolation(float v11, float v12, float v21, float v22, float x1, float y1, float x2, float y2, float x, float y)
    {
        var d = (x2 - x1) * (y2 - y1);
        var w11 = (x2 - x) * (y2 - y) / d;
        var w12 = (x2 - x) * (y - y1) / d;
        var w21 = (x - x1) * (y2 - y) / d;
        var w22 = (x - x1) * (y - y1) / d;
        return (w11 * v11) + (w12 * v12) + (w21 * v21) + (w22 * v22);
    }
}
