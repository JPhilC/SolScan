using SolScan.Core.Camera;
using SolScan.Infrastructure.Capture;
using SolScan.Processing.Capture;

namespace SolScan.Tests;

public class SerCropperTests
{
    private static (string Source, string Output) MakeTempPaths()
    {
        var id = Guid.NewGuid().ToString("N");
        return (
            Path.Combine(Path.GetTempPath(), $"solscan-test-{id}-source.ser"),
            Path.Combine(Path.GetTempPath(), $"solscan-test-{id}-cropped.ser"));
    }

    /// <summary>Writes a source file whose every row of every frame is filled with a distinct byte
    /// value equal to its own row index - lets a test assert exactly which rows survived the crop by
    /// value alone, without needing to separately track offsets.</summary>
    private static void WriteRowIndexedSource(string path, int width, int height, int bitDepth, int frameCount)
    {
        var bytesPerPixel = bitDepth <= 8 ? 1 : 2;
        using var writer = new SerWriter();
        writer.Open(path, width, height, bitDepth);
        for (var i = 0; i < frameCount; i++)
        {
            var data = new byte[width * height * bytesPerPixel];
            for (var y = 0; y < height; y++)
            {
                var rowStart = y * width * bytesPerPixel;
                for (var b = 0; b < width * bytesPerPixel; b++)
                {
                    data[rowStart + b] = (byte)y;
                }
            }

            writer.WriteFrame(new CameraFrame(data, width, height, bitDepth, DateTime.UtcNow));
        }

        writer.Close();
    }

    [Fact]
    public async Task CropAsync_KeepsACenteredSliceOfEachFrameAtFullWidth()
    {
        const int width = 6;
        const int height = 100;
        const int bitDepth = 8;
        const int frameCount = 3;
        var (source, output) = MakeTempPaths();

        try
        {
            WriteRowIndexedSource(source, width, height, bitDepth, frameCount);

            var cropper = new SerCropper(() => new SerReader(), () => new SerWriter());
            var result = await cropper.CropAsync(source, output, heightFraction: 0.2);

            Assert.Equal(frameCount, result.FrameCount);
            Assert.Equal(width, result.Width);
            Assert.Equal(height, result.SourceHeight);
            Assert.Equal(20, result.CroppedHeight);
            Assert.Equal(40, result.RowOffset); // (100 - 20) / 2

            using var reader = new SerReader();
            reader.Open(output);
            Assert.Equal(width, reader.Header.Width);
            Assert.Equal(20, reader.Header.Height);
            Assert.Equal(bitDepth, reader.Header.PixelDepth);
            Assert.Equal(frameCount, reader.Header.FrameCount);

            for (var i = 0; i < frameCount; i++)
            {
                var frame = reader.ReadFrame(i);
                Assert.Equal(width * 20, frame.Data.Length);
                for (var y = 0; y < 20; y++)
                {
                    var expectedSourceRow = (byte)(40 + y);
                    for (var x = 0; x < width; x++)
                    {
                        Assert.Equal(expectedSourceRow, frame.Data[y * width + x]);
                    }
                }
            }
        }
        finally
        {
            File.Delete(source);
            File.Delete(output);
        }
    }

    [Fact]
    public async Task CropAsync_Mono16Frame_CropsWithTwoBytesPerPixel()
    {
        const int width = 4;
        const int height = 50;
        const int bitDepth = 16;
        var (source, output) = MakeTempPaths();

        try
        {
            WriteRowIndexedSource(source, width, height, bitDepth, frameCount: 1);

            var cropper = new SerCropper(() => new SerReader(), () => new SerWriter());
            var result = await cropper.CropAsync(source, output, heightFraction: 0.5);

            Assert.Equal(25, result.CroppedHeight);

            using var reader = new SerReader();
            reader.Open(output);
            var frame = reader.ReadFrame(0);
            Assert.Equal(width * 25 * 2, frame.Data.Length);
        }
        finally
        {
            File.Delete(source);
            File.Delete(output);
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public async Task CropAsync_InvalidHeightFraction_Throws(double heightFraction)
    {
        var (source, output) = MakeTempPaths();

        try
        {
            WriteRowIndexedSource(source, width: 4, height: 10, bitDepth: 8, frameCount: 1);

            var cropper = new SerCropper(() => new SerReader(), () => new SerWriter());
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => cropper.CropAsync(source, output, heightFraction));
        }
        finally
        {
            File.Delete(source);
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    [Fact]
    public void ComputeCroppedHeight_ClampsToAtLeastOneRow()
    {
        Assert.Equal(1, SerCropper.ComputeCroppedHeight(sourceHeight: 100, heightFraction: 0.001));
    }

    [Fact]
    public void ComputeCroppedHeight_FullFraction_KeepsTheWholeHeight()
    {
        Assert.Equal(100, SerCropper.ComputeCroppedHeight(sourceHeight: 100, heightFraction: 1.0));
    }
}
