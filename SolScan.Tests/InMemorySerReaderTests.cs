using SolScan.Core.Camera;
using SolScan.Infrastructure.Capture;
using SolScan.Processing.Shg;

namespace SolScan.Tests;

public class InMemorySerReaderTests
{
    [Fact]
    public void LoadFrom_RoundTripsRealFrameDataFromARealSerReader()
    {
        const int width = 6;
        const int height = 4;
        const int bitDepth = 16;
        const int frameCount = 5;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            using (var writer = new SerWriter())
            {
                writer.Open(path, width, height, bitDepth);
                for (var i = 0; i < frameCount; i++)
                {
                    var data = new byte[width * height * 2];
                    // Every frame filled with a distinct value, so a mixed-up index would be obvious.
                    var value = (ushort)(1000 * (i + 1));
                    for (var b = 0; b < data.Length; b += 2)
                    {
                        data[b] = (byte)(value & 0xFF);
                        data[b + 1] = (byte)((value >> 8) & 0xFF);
                    }

                    writer.WriteFrame(new CameraFrame(data, width, height, bitDepth, DateTime.UtcNow));
                }

                writer.Close();
            }

            using var realReader = new SerReader();
            realReader.Open(path);

            var inMemory = InMemorySerReader.LoadFrom(realReader);

            Assert.Equal(realReader.Header, inMemory.Header);

            for (var i = 0; i < frameCount; i++)
            {
                var expected = realReader.ReadFrame(i, includeTimestamp: false);
                var actual = inMemory.ReadFrame(i);

                Assert.Equal(expected.Data, actual.Data);
                Assert.Equal(expected.Width, actual.Width);
                Assert.Equal(expected.Height, actual.Height);
                Assert.Equal(expected.BitDepth, actual.BitDepth);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadFrame_NeverCarriesARealTimestamp()
    {
        const int width = 2;
        const int height = 2;
        const int bitDepth = 8;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            using (var writer = new SerWriter())
            {
                writer.Open(path, width, height, bitDepth);
                writer.WriteFrame(new CameraFrame(new byte[width * height], width, height, bitDepth, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
                writer.Close();
            }

            using var realReader = new SerReader();
            realReader.Open(path);
            var inMemory = InMemorySerReader.LoadFrom(realReader);

            // includeTimestamp: true is accepted (interface compatibility) but has no effect - this
            // type simply never carries real timestamps, by design (see its own doc comment).
            Assert.Equal(DateTime.MinValue, inMemory.ReadFrame(0, includeTimestamp: true).TimestampUtc);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_IsNotSupported()
    {
        const int width = 2;
        const int height = 2;
        const int bitDepth = 8;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            using (var writer = new SerWriter())
            {
                writer.Open(path, width, height, bitDepth);
                writer.WriteFrame(new CameraFrame(new byte[width * height], width, height, bitDepth, DateTime.UtcNow));
                writer.Close();
            }

            using var realReader = new SerReader();
            realReader.Open(path);
            var inMemory = InMemorySerReader.LoadFrom(realReader);

            Assert.Throws<NotSupportedException>(() => inMemory.Open(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadFrame_CalledConcurrentlyFromMultipleThreads_ReturnsCorrectDataForEachIndex()
    {
        const int width = 8;
        const int height = 6;
        const int bitDepth = 8;
        const int frameCount = 64;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            using (var writer = new SerWriter())
            {
                writer.Open(path, width, height, bitDepth);
                for (var i = 0; i < frameCount; i++)
                {
                    var data = new byte[width * height];
                    Array.Fill(data, (byte)i);
                    writer.WriteFrame(new CameraFrame(data, width, height, bitDepth, DateTime.UtcNow));
                }

                writer.Close();
            }

            using var realReader = new SerReader();
            realReader.Open(path);
            var inMemory = InMemorySerReader.LoadFrom(realReader);

            const int passes = 20;
            Parallel.For(0, frameCount * passes, n =>
            {
                var index = n % frameCount;
                var frame = inMemory.ReadFrame(index);
                Assert.Equal(width * height, frame.Data.Length);
                Assert.All(frame.Data, b => Assert.Equal((byte)index, b));
            });
        }
        finally
        {
            File.Delete(path);
        }
    }
}
