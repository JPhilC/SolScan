using SolScan.Core.Camera;
using SolScan.Infrastructure.Capture;

namespace SolScan.Tests;

public class SerReaderTests
{
    [Fact]
    public void RoundTrip_HeaderAndFramesMatchWhatSerWriterWrote()
    {
        const int width = 4;
        const int height = 3;
        const int bitDepth = 16;
        const int frameCount = 5;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");
        var timestamps = new DateTime[frameCount];

        try
        {
            using (var writer = new SerWriter())
            {
                writer.Open(path, width, height, bitDepth);
                for (var i = 0; i < frameCount; i++)
                {
                    var data = new byte[width * height * 2];
                    Array.Fill(data, (byte)(i + 1));
                    timestamps[i] = new DateTime(2025, 8, 31, 16, 14, 27, DateTimeKind.Utc).AddSeconds(i);
                    writer.WriteFrame(new CameraFrame(data, width, height, bitDepth, timestamps[i]));
                }

                writer.Close();
            }

            using var reader = new SerReader();
            reader.Open(path);

            Assert.Equal("LUCAM-RECORDER", reader.Header.FileId);
            Assert.Equal(width, reader.Header.Width);
            Assert.Equal(height, reader.Header.Height);
            Assert.Equal(bitDepth, reader.Header.PixelDepth);
            Assert.Equal(frameCount, reader.Header.FrameCount);
            Assert.True(reader.Header.LittleEndian);

            for (var i = 0; i < frameCount; i++)
            {
                var frame = reader.ReadFrame(i);
                Assert.Equal(width * height * 2, frame.Data.Length);
                Assert.All(frame.Data, b => Assert.Equal((byte)(i + 1), b));
                Assert.Equal(timestamps[i].Ticks, frame.TimestampUtc.Ticks);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadFrame_TruncatedFileWithNoTrailer_FallsBackToMinValueTimestamp()
    {
        const int width = 2;
        const int height = 2;
        const int bitDepth = 8;
        const int frameCount = 2;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            using (var writer = new SerWriter())
            {
                writer.Open(path, width, height, bitDepth);
                for (var i = 0; i < frameCount; i++)
                {
                    writer.WriteFrame(new CameraFrame(new byte[width * height], width, height, bitDepth, DateTime.UtcNow));
                }

                writer.Close();
            }

            // Truncate off the trailer entirely (header + frame data only) - simulates a real-world
            // file (e.g. some Sunscan captures) that never had per-frame timestamps to begin with.
            const int headerSize = 178;
            var truncatedLength = headerSize + (frameCount * width * height);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                stream.SetLength(truncatedLength);
            }

            using var reader = new SerReader();
            reader.Open(path);

            var frame = reader.ReadFrame(0);
            Assert.Equal(DateTime.MinValue, frame.TimestampUtc);
            Assert.Equal(width * height, frame.Data.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_FileTooShortForHeader_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");
        File.WriteAllBytes(path, new byte[10]);

        try
        {
            using var reader = new SerReader();
            Assert.Throws<InvalidDataException>(() => reader.Open(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
