using SolScan.Processing.Math;
using SolScan.Processing.Stretching;

namespace SolScan.Tests;

public class StretchingTests
{
    [Fact]
    public void ClaheStrategy_Stretch_KeepsOutputWithinRangeAndIncreasesLocalContrast()
    {
        // A repeating 8-level ramp (period matching the 8px tile size, so every individual tile sees
        // the exact same local gradient and has real texture to equalize), confined to roughly the
        // bottom 58% of the value range (10000-38000 out of 65535) - narrow enough that a plain global
        // stretch alone wouldn't reach the top of the range, but with each of the 8 per-tile levels far
        // enough apart (4000 apart) to land in clearly distinct histogram bins even after
        // RangeExpansionStrategy's own max-anchored rescale (a tighter spacing risks several levels
        // aliasing into the same or adjacent bins at only 64 bins of resolution, which would defeat the
        // point of this test rather than exercise CLAHE's real behaviour).
        const int width = 64;
        const int height = 64;
        const int tileSize = 8;
        var data = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = 10000 + ((x % tileSize) * 4000);
            }
        }

        new ClaheStrategy(tileSize, 64, 1.0).Stretch(data);

        float min = float.MaxValue, max = float.MinValue;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                min = Math.Min(min, data[y, x]);
                max = Math.Max(max, data[y, x]);
                Assert.InRange(data[y, x], 0, 65535);
            }
        }

        // A 28000-out-of-65535 local ramp should come out spread noticeably wider than that after
        // per-tile equalization redistributes it across (most of) the full output range.
        Assert.True(max - min > 40000, $"Expected CLAHE to spread a local per-tile ramp out considerably, but the range was only {min}-{max}.");
    }

    [Fact]
    public void AutoStretchStrategy_Stretch_WithoutAnEllipse_StillProducesAValidInRangeImage()
    {
        // No ellipse metadata (e.g. a frame with no detected disk) skips the disk-aware machinery
        // entirely but should still run the brightness/cutoff steps without throwing.
        const int width = 32;
        const int height = 32;
        var data = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = 1000 + (x * 10) + (y * 5);
            }
        }

        AutoStretchStrategy.Stretch(data, ellipse: null);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Assert.InRange(data[y, x], 0, 65535);
            }
        }
    }

    [Fact]
    public void AutoStretchStrategy_Stretch_WithAnEllipse_BrightensADimDiskTowardTheMidtoneTarget()
    {
        // A dim (but not zero) disk on a dark background, confined to a low part of the 16-bit range -
        // AutoStretch's brightness-matching step should lift the disk's own median well above where it
        // started, similar in spirit to DiskGeometryCorrectorTests' low-dynamic-range regression case.
        const int width = 80;
        const int height = 80;
        const double cx = 40, cy = 40, r = 25;
        var data = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                data[y, x] = (dx * dx) + (dy * dy) <= r * r ? 3000 : 500;
            }
        }

        // A*x^2 + B*x*y + C*y^2 + D*x + E*y + F = 0 for a circle centered at (cx,cy) with radius r:
        // (x-cx)^2 + (y-cy)^2 - r^2 = 0 => x^2 + y^2 - 2cx*x - 2cy*y + (cx^2+cy^2-r^2) = 0
        var ellipse = new Ellipse(1, 0, 1, -2 * cx, -2 * cy, (cx * cx) + (cy * cy) - (r * r));

        var diskMedianBefore = 3000.0;
        AutoStretchStrategy.Stretch(data, ellipse);

        double sum = 0;
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Assert.InRange(data[y, x], 0, 65535);
                var dx = x - cx;
                var dy = y - cy;
                if ((dx * dx) + (dy * dy) <= r * r)
                {
                    sum += data[y, x];
                    count++;
                }
            }
        }

        var diskAverageAfter = sum / count;
        Assert.True(diskAverageAfter > diskMedianBefore * 2, $"Expected AutoStretch to noticeably brighten a dim disk, but its average only went from {diskMedianBefore} to {diskAverageAfter}.");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(0)]
    [InlineData(-1)]
    public void AutoStretchStrategy_Stretch_ThrowsForGammaNotGreaterThanOne(double gamma)
    {
        var data = new float[8, 8];
        Assert.Throws<ArgumentOutOfRangeException>(() => AutoStretchStrategy.Stretch(data, ellipse: null, gamma: gamma));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void AutoStretchStrategy_Stretch_ThrowsForBackgroundThresholdOutOfRange(double backgroundThreshold)
    {
        var data = new float[8, 8];
        Assert.Throws<ArgumentOutOfRangeException>(() => AutoStretchStrategy.Stretch(data, ellipse: null, backgroundThreshold: backgroundThreshold));
    }

    [Fact]
    public void AutoStretchStrategy_Stretch_ThrowsForNegativeProtusStretch()
    {
        var data = new float[8, 8];
        Assert.Throws<ArgumentOutOfRangeException>(() => AutoStretchStrategy.Stretch(data, ellipse: null, protusStretch: -0.1));
    }

    [Fact]
    public void MultiScaleClaheStrategy_Stretch_WithoutAnEllipse_StillProducesAValidInRangeImage()
    {
        // No ellipse metadata falls back to sizing the largest tile off the whole image instead of the
        // disk's diameter, but should still run without throwing.
        const int width = 64;
        const int height = 64;
        var data = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = 10000 + ((x % 8) * 4000);
            }
        }

        MultiScaleClaheStrategy.Stretch(data, ellipse: null);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Assert.InRange(data[y, x], 0, 65535);
            }
        }
    }

    [Fact]
    public void MultiScaleClaheStrategy_Stretch_WithAnEllipse_KeepsOutputInRangeAndIncreasesLocalContrast()
    {
        // Same low-contrast repeating-ramp setup as the plain ClaheStrategy test above - multi-scale
        // CLAHE averages several single-scale passes together, so it should still spread this out
        // noticeably even if not as aggressively as a single well-matched tile size would alone.
        const int width = 128;
        const int height = 128;
        var data = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = 10000 + ((x % 8) * 4000);
            }
        }

        var ellipse = new Ellipse(1, 0, 1, -2 * 64, -2 * 64, (64.0 * 64) + (64.0 * 64) - (40.0 * 40));

        MultiScaleClaheStrategy.Stretch(data, ellipse);

        float min = float.MaxValue, max = float.MinValue;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                min = Math.Min(min, data[y, x]);
                max = Math.Max(max, data[y, x]);
                Assert.InRange(data[y, x], 0, 65535);
            }
        }

        Assert.True(max - min > 20000, $"Expected multi-scale CLAHE to spread a local per-tile ramp out considerably, but the range was only {min}-{max}.");
    }
}
