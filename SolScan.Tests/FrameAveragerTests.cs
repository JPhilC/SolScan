using SolScan.Core.Camera;
using SolScan.Infrastructure.Capture;
using SolScan.Processing.Shg;

namespace SolScan.Tests;

public class FrameAveragerTests
{
    [Fact]
    public void ComputeAverage_ExcludesFramesBelowHalfOfMaxMean()
    {
        const int width = 4;
        const int height = 3;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            using (var writer = new SerWriter())
            {
                writer.Open(path, width, height, 16);

                // Two dim frames (well under 50% of the bright frames' mean) - should be excluded.
                WriteConstantFrame(writer, width, height, value: 100);
                WriteConstantFrame(writer, width, height, value: 100);

                // Three bright frames, all the same value - should be the only ones averaged.
                WriteConstantFrame(writer, width, height, value: 4000);
                WriteConstantFrame(writer, width, height, value: 4000);
                WriteConstantFrame(writer, width, height, value: 4000);

                writer.Close();
            }

            using var reader = new SerReader();
            reader.Open(path);

            var average = new FrameAverager().ComputeAverage(reader);

            Assert.Equal(height, average.GetLength(0));
            Assert.Equal(width, average.GetLength(1));
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    Assert.Equal(4000.0, average[y, x], precision: 3);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ComputeAverage_MixedBrightness_AveragesOnlyTheBrightGroup()
    {
        const int width = 2;
        const int height = 2;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            using (var writer = new SerWriter())
            {
                writer.Open(path, width, height, 16);
                WriteConstantFrame(writer, width, height, value: 50); // excluded
                WriteConstantFrame(writer, width, height, value: 2000); // included
                WriteConstantFrame(writer, width, height, value: 3000); // included (the max)
                writer.Close();
            }

            using var reader = new SerReader();
            reader.Open(path);

            var average = new FrameAverager().ComputeAverage(reader);

            // threshold = 0.5 * 3000 = 1500, so only the 2000/3000 frames qualify -> average 2500.
            Assert.Equal(2500.0, average[0, 0], precision: 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void WriteConstantFrame(SerWriter writer, int width, int height, ushort value)
    {
        var data = new byte[width * height * 2];
        for (var i = 0; i < data.Length; i += 2)
        {
            data[i] = (byte)(value & 0xFF);
            data[i + 1] = (byte)((value >> 8) & 0xFF);
        }

        writer.WriteFrame(new CameraFrame(data, width, height, 16, DateTime.UtcNow));
    }
}
