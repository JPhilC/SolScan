using SolScan.Core.Camera;

namespace SolScan.Tests;

public class FrameCentroidTests
{
    /// <summary>A frame that is <paramref name="background"/> everywhere except columns
    /// [bandStart, bandEnd), which are <paramref name="bandValue"/> - a Sun chord lighting part of the slit.</summary>
    private static CameraFrame BandFrame(int width, int height, int bandStart, int bandEnd, int bitDepth, int background, int bandValue)
    {
        var bytesPerPixel = bitDepth > 8 ? 2 : 1;
        var data = new byte[width * height * bytesPerPixel];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = x >= bandStart && x < bandEnd ? bandValue : background;
                var index = ((y * width) + x) * bytesPerPixel;
                data[index] = (byte)(value & 0xFF);
                if (bytesPerPixel == 2)
                    data[index + 1] = (byte)(value >> 8);
            }
        }
        return new CameraFrame(data, width, height, bitDepth, DateTime.UtcNow);
    }

    [Fact]
    public void CentredBand_HasCentreXNearHalf()
    {
        var stats = FrameCentroid.Measure(BandFrame(400, 100, 100, 300, 16, 100, 40000));

        Assert.True(stats.HasSignal);
        Assert.Equal(0.5, stats.CentreX, precision: 2);
    }

    [Fact]
    public void BandLeftOfCentre_HasCentreXBelowHalf_RightOfCentre_Above()
    {
        var left = FrameCentroid.Measure(BandFrame(400, 100, 20, 120, 16, 100, 40000));
        var right = FrameCentroid.Measure(BandFrame(400, 100, 280, 380, 16, 100, 40000));

        Assert.True(left.CentreX < 0.3);
        Assert.True(right.CentreX > 0.7);
    }

    [Fact]
    public void EightBitFrame_IsHandledToo()
    {
        var stats = FrameCentroid.Measure(BandFrame(400, 100, 100, 300, 8, 2, 200));

        Assert.True(stats.HasSignal);
        Assert.Equal(0.5, stats.CentreX, precision: 2);
    }

    [Fact]
    public void FlatFrame_HasNoSignal()
    {
        var stats = FrameCentroid.Measure(BandFrame(400, 100, 0, 0, 16, 300, 300));

        Assert.False(stats.HasSignal);
        Assert.True(double.IsNaN(stats.CentreX));
    }

    [Fact]
    public void FaintNoise_BelowTheSignalFloor_HasNoSignal()
    {
        // Rises only ~0.1% of full scale above the background - noise, not a Sun.
        var stats = FrameCentroid.Measure(BandFrame(400, 100, 100, 300, 16, 100, 165));

        Assert.False(stats.HasSignal);
    }
}
