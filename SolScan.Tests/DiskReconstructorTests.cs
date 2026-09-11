using SolScan.Core.Camera;
using SolScan.Infrastructure.Capture;
using SolScan.Processing.Math;
using SolScan.Processing.Shg;

namespace SolScan.Tests;

public class DiskReconstructorTests
{
    [Fact]
    public void Reconstruct_LinearRampAwayFromEdges_ReturnsExactRowValue()
    {
        const int width = 5;
        const int height = 10;
        const int frameCount = 2;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            WriteRampFrames(path, width, height, frameCount);

            using var reader = new SerReader();
            reader.Open(path);

            var polynomial = new QuadraticPolynomial(0, 0, 5); // constant row 5, well within [0,10)
            var output = new DiskReconstructor().Reconstruct(reader, polynomial, pixelShift: 0);

            Assert.Equal(frameCount, output.GetLength(0));
            Assert.Equal(width, output.GetLength(1));
            for (var frame = 0; frame < frameCount; frame++)
            {
                for (var x = 0; x < width; x++)
                {
                    // source[y,x] = y for every column, so the symmetric Gaussian blend of a linear
                    // function around row 5 is exactly its value at row 5.
                    Assert.Equal(5.0, output[frame, x], precision: 3);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Reconstruct_NearTopEdge_SkipsOutOfBoundsTapsRatherThanClamping()
    {
        const int width = 3;
        const int height = 10;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            WriteRampFrames(path, width, height, frameCount: 1);

            using var reader = new SerReader();
            reader.Open(path);

            var polynomial = new QuadraticPolynomial(0, 0, 0); // row 0 - right at the top edge
            var output = new DiskReconstructor().Reconstruct(reader, polynomial, pixelShift: 0);

            // Only dy in {0, 0.5, 1} land in-bounds (dy=-1/-0.5 would need y1=-1). Expected value
            // computed independently of the implementation, as a check on the renormalized (not
            // edge-clamped) blend.
            double value = 0;
            double weightSum = 0;
            foreach (var dy in new[] { 0.0, 0.5, 1.0 })
            {
                var y1 = (int)Math.Floor(dy);
                var y2 = y1 + 1;
                var frac = dy - y1;
                var weight = Math.Exp(-0.5 * dy * dy);
                var yValue = ((1 - frac) * y1) + (frac * y2); // source[y,x] = y
                value += weight * yValue;
                weightSum += weight;
            }

            var expected = value / weightSum;
            Assert.Equal(expected, output[0, 0], precision: 6);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Writes <paramref name="frameCount"/> identical frames where every pixel's value
    /// equals its own row index (source[y,x] = y) - a simple, linear, easy-to-hand-verify pattern.</summary>
    private static void WriteRampFrames(string path, int width, int height, int frameCount)
    {
        using var writer = new SerWriter();
        writer.Open(path, width, height, 16);
        for (var f = 0; f < frameCount; f++)
        {
            var data = new byte[width * height * 2];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = ((y * width) + x) * 2;
                    var value = (ushort)y;
                    data[index] = (byte)(value & 0xFF);
                    data[index + 1] = (byte)((value >> 8) & 0xFF);
                }
            }

            writer.WriteFrame(new CameraFrame(data, width, height, 16, DateTime.UtcNow));
        }

        writer.Close();
    }
}
