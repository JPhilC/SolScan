using System.Text;
using SolScan.Core.Camera;
using SolScan.Infrastructure.Capture;

namespace SolScan.Tests;

public class SerWriterTests
{
    [Fact]
    public void WritesHeaderAndFrames_MatchingSerLayout()
    {
        const int width = 4;
        const int height = 3;
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
                    writer.WriteFrame(new CameraFrame(data, width, height, bitDepth, DateTime.UtcNow));
                }

                writer.Close();
            }

            var bytes = File.ReadAllBytes(path);

            const int headerSize = 178;
            var frameBytes = width * height * 2;
            var expectedSize = headerSize + (frameCount * frameBytes) + (frameCount * 8); // + per-frame UTC-ticks trailer
            Assert.Equal(expectedSize, bytes.Length);

            Assert.Equal("LUCAM-RECORDER", Encoding.ASCII.GetString(bytes, 0, 14));
            Assert.Equal(width, BitConverter.ToInt32(bytes, 14 + 4 + 4 + 4));
            Assert.Equal(height, BitConverter.ToInt32(bytes, 14 + 4 + 4 + 4 + 4));
            Assert.Equal(bitDepth, BitConverter.ToInt32(bytes, 14 + 4 + 4 + 4 + 4 + 4));
            Assert.Equal(frameCount, BitConverter.ToInt32(bytes, 14 + 4 + 4 + 4 + 4 + 4 + 4));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
