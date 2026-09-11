using SolScan.Core.Camera;
using SolScan.Core.Processing;
using SolScan.Infrastructure.Capture;
using SolScan.Processing.Shg;

namespace SolScan.Tests;

public class ShgProcessorTests
{
    [Fact]
    public async Task ProcessAsync_ProducesRequestedImages_AndReportsUnimplementedKindsAsSkipped()
    {
        const int width = 20;
        const int height = 15;
        const int frameCount = 5;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            WriteSyntheticFile(path, width, height, frameCount, lineRow: 7, background: 1000, depth: 800, sigma: 1.5);

            var processor = new ShgProcessor(() => new SerReader());
            var defaults = ProcessParams.CreateDefault();
            var processParams = defaults with
            {
                RequestedImages = new RequestedImages([
                    GeneratedImageKind.Raw,
                    GeneratedImageKind.Continuum,
                    GeneratedImageKind.GeometryCorrected,
                ]),
                SpectrumParams = defaults.SpectrumParams with { PixelShift = 0, ContinuumShift = 2 },
            };

            var result = await processor.ProcessAsync(path, processParams);

            Assert.Equal(2, result.Images.Count);

            var raw = Assert.Single(result.Images, i => i.Kind == GeneratedImageKind.Raw);
            Assert.Equal(width, raw.Width);
            Assert.Equal(frameCount, raw.Height);

            var continuum = Assert.Single(result.Images, i => i.Kind == GeneratedImageKind.Continuum);
            Assert.Equal(width, continuum.Width);
            Assert.Equal(frameCount, continuum.Height);

            var skipped = Assert.Single(result.SkippedKinds);
            Assert.Equal(GeneratedImageKind.GeometryCorrected, skipped);

            Assert.True(result.DetectedLinePolynomial.HasValue);
            var midRow = result.DetectedLinePolynomial!.Value.Evaluate(width / 2.0);
            Assert.InRange(midRow, 5, 9);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ProcessAsync_NothingRequestedFromTheUnimplementedOrReconstructionSet_ReturnsNoImagesNoPolynomial()
    {
        const int width = 10;
        const int height = 8;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            WriteSyntheticFile(path, width, height, frameCount: 2, lineRow: 4, background: 500, depth: 300, sigma: 1.0);

            var processor = new ShgProcessor(() => new SerReader());
            var processParams = ProcessParams.CreateDefault() with { RequestedImages = new RequestedImages([]) };

            var result = await processor.ProcessAsync(path, processParams);

            Assert.Empty(result.Images);
            Assert.Empty(result.SkippedKinds);
            Assert.Null(result.DetectedLinePolynomial);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void WriteSyntheticFile(string path, int width, int height, int frameCount, int lineRow, double background, double depth, double sigma)
    {
        using var writer = new SerWriter();
        writer.Open(path, width, height, 16);
        for (var f = 0; f < frameCount; f++)
        {
            var data = new byte[width * height * 2];
            for (var y = 0; y < height; y++)
            {
                var dy = y - lineRow;
                var value = (ushort)Math.Clamp(background - (depth * Math.Exp(-(dy * dy) / (2 * sigma * sigma))), 0, 65535);
                for (var x = 0; x < width; x++)
                {
                    var index = ((y * width) + x) * 2;
                    data[index] = (byte)(value & 0xFF);
                    data[index + 1] = (byte)((value >> 8) & 0xFF);
                }
            }

            writer.WriteFrame(new CameraFrame(data, width, height, 16, DateTime.UtcNow));
        }

        writer.Close();
    }
}
