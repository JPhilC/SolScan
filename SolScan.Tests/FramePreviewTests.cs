using SolScan.Core.Camera;

namespace SolScan.Tests;

public class FramePreviewTests
{
    [Fact]
    public void ComputeHistogram_BucketsEightBitSamplesByValue()
    {
        var data = new byte[] { 0, 128, 255, 255 };
        var frame = new CameraFrame(data, 4, 1, 8, DateTime.UtcNow);

        var histogram = FramePreview.ComputeHistogram(frame);

        Assert.Equal(FramePreview.HistogramBucketCount, histogram.Length);
        Assert.Equal(1, histogram[0]);
        Assert.Equal(1, histogram[128]);
        Assert.Equal(2, histogram[255]);
        Assert.Equal(4, histogram.Sum());
    }

    [Fact]
    public void Stretch_MapsBlackAndWhitePointsToFullEightBitRange()
    {
        var data = new byte[] { 64, 128, 192 };
        var frame = new CameraFrame(data, 3, 1, 8, DateTime.UtcNow);

        var (stretched, width, height) = FramePreview.Stretch(frame, 64.0 / 255, 192.0 / 255);

        Assert.Equal(3, width);
        Assert.Equal(1, height);
        Assert.Equal(0, stretched[0]);
        Assert.Equal(255, stretched[2]);
        Assert.InRange(stretched[1], (byte)120, (byte)135); // midpoint, allow rounding slack
    }

    [Fact]
    public void Stretch_HandlesSixteenBitLittleEndianSamples()
    {
        var data = new byte[] { 0x00, 0x00, 0xFF, 0xFF }; // 0, 65535
        var frame = new CameraFrame(data, 2, 1, 16, DateTime.UtcNow);

        var (stretched, width, height) = FramePreview.Stretch(frame, 0, 1);

        Assert.Equal(2, width);
        Assert.Equal(1, height);
        Assert.Equal(0, stretched[0]);
        Assert.Equal(255, stretched[1]);
    }

    [Fact]
    public void ComputeHistogramAndStretch_DownsampleFullSizeFrameToMaxDimension()
    {
        // A stand-in for the ASI678MM's real 3840x2160 - checks the preview is actually being
        // downsampled (see FramePreview's class doc comment for why: a live preview has no
        // business scanning every one of several million sensor pixels per redraw).
        const int width = 3840;
        const int height = 2160;
        var data = new byte[width * height]; // all zero (black) - content doesn't matter here
        var frame = new CameraFrame(data, width, height, 8, DateTime.UtcNow);

        var histogram = FramePreview.ComputeHistogram(frame, maxDimension: 960);
        var (stretched, outWidth, outHeight) = FramePreview.Stretch(frame, 0, 1, maxDimension: 960);

        Assert.Equal(960, outWidth); // 3840 / 4
        Assert.Equal(540, outHeight); // 2160 / 4
        Assert.Equal(outWidth * outHeight, stretched.Length);
        Assert.Equal(width * height, histogram.Sum() * 16); // every sampled pixel stands in for a 4x4 block
    }

    [Fact]
    public void ComputeHistogramStats_ReportsRawMinMaxAverageAndBitDepth()
    {
        // 12-bit-in-16-bit values, little-endian, matching the ASI678MM's actual RAW16 output
        // (see AsiCameraDevice's remarks) - values are the *raw* sample, not normalized to 0-1.
        var data = new byte[]
        {
            0x0C, 0x00, // 12
            0x88, 0x03, // 904
            0xFF, 0x0F, // 4095
        };
        var frame = new CameraFrame(data, 3, 1, 12, DateTime.UtcNow);

        var stats = FramePreview.ComputeHistogramStats(frame);

        Assert.Equal(12, stats.BitDepth);
        Assert.Equal(12, stats.MinValue);
        Assert.Equal(4095, stats.MaxValue);
        Assert.Equal((12 + 904 + 4095) / 3.0, stats.AverageValue, precision: 6);
        Assert.Equal(FramePreview.HistogramBucketCount, stats.Histogram.Length);
        Assert.Equal(3, stats.Histogram.Sum());
    }

    [Fact]
    public void ComputeAutoStretch_ReturnsFullRange_WhenHistogramIsEmpty()
    {
        var (blackPoint, whitePoint) = FramePreview.ComputeAutoStretch(new int[FramePreview.HistogramBucketCount]);

        Assert.Equal(0, blackPoint);
        Assert.Equal(1, whitePoint);
    }

    [Fact]
    public void ComputeAutoStretch_ClipsOutliersAtBothEndsInsteadOfUsingTrueMinMax()
    {
        // A handful of dead/hot-pixel outliers at each extreme, with the real scene content
        // concentrated in a narrow middle band - a stand-in for a typical low-dynamic-range scene
        // (see FramePreview.ComputeAutoStretch's doc comment).
        var histogram = new int[FramePreview.HistogramBucketCount];
        histogram[0] = 5;
        for (var bucket = 20; bucket <= 29; bucket++)
        {
            histogram[bucket] = 999;
        }
        histogram[255] = 5;

        var (blackPoint, whitePoint) = FramePreview.ComputeAutoStretch(histogram, clipFraction: 0.01);

        const int lastBucketIndex = FramePreview.HistogramBucketCount - 1;
        Assert.Equal(20.0 / lastBucketIndex, blackPoint, precision: 6);
        Assert.Equal(29.0 / lastBucketIndex, whitePoint, precision: 6);
    }
}
