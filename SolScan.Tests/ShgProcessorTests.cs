using SolScan.Core.Camera;
using SolScan.Core.Equipment;
using SolScan.Core.Processing;
using SolScan.Infrastructure.Capture;
using SolScan.Processing.Shg;
using SolScan.Processing.Spectrum;

namespace SolScan.Tests;

public class ShgProcessorTests
{
    private static readonly SpectrographProfile Instrument = SpectrographProfile.CreateSolEx();
    private const double PixelSizeMicrons = 2.0;
    [Fact]
    public async Task ProcessAsync_ProducesRequestedImages()
    {
        // This file's brightness only varies along the spectral-line row axis, not across x/frame -
        // deliberately no disk shape at all, so it only exercises line-curvature detection/
        // reconstruction, not disk-edge detection/geometry correction (see DiskGeometryCorrectorTests
        // for the elliptical-disk file that exercises GeometryCorrected/GeometryCorrectedProcessed).
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

            Assert.Empty(result.SkippedKinds);

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
    public async Task ProcessAsync_CachingInMemoryProducesTheSameOutputAsTheDiskFallbackPath()
    {
        // Regression check for the "eliminate redundant disk re-reads" optimization: whether the
        // recording is cached in memory (availableMemoryBytesProvider reporting plenty) or re-read from
        // disk at each stage (reporting none, forcing the fallback branch - see ShgProcessor's own doc
        // comment) must produce numerically identical output, since this is purely an execution-strategy
        // change, never an algorithm change.
        const int width = 20;
        const int height = 15;
        const int frameCount = 5;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            WriteSyntheticFile(path, width, height, frameCount, lineRow: 7, background: 1000, depth: 800, sigma: 1.5);

            var defaults = ProcessParams.CreateDefault();
            var processParams = defaults with
            {
                RequestedImages = new RequestedImages([GeneratedImageKind.Raw, GeneratedImageKind.Continuum]),
                SpectrumParams = defaults.SpectrumParams with { PixelShift = 0, ContinuumShift = 2 },
            };

            var cachingProcessor = new ShgProcessor(() => new SerReader(), availableMemoryBytesProvider: () => long.MaxValue);
            var fallbackProcessor = new ShgProcessor(() => new SerReader(), availableMemoryBytesProvider: () => 0);

            var cached = await cachingProcessor.ProcessAsync(path, processParams);
            var fallback = await fallbackProcessor.ProcessAsync(path, processParams);

            var cachedRaw = Assert.Single(cached.Images, i => i.Kind == GeneratedImageKind.Raw);
            var fallbackRaw = Assert.Single(fallback.Images, i => i.Kind == GeneratedImageKind.Raw);
            AssertSamePixels(cachedRaw.Pixels, fallbackRaw.Pixels, width, frameCount);

            var cachedContinuum = Assert.Single(cached.Images, i => i.Kind == GeneratedImageKind.Continuum);
            var fallbackContinuum = Assert.Single(fallback.Images, i => i.Kind == GeneratedImageKind.Continuum);
            AssertSamePixels(cachedContinuum.Pixels, fallbackContinuum.Pixels, width, frameCount);

            Assert.Equal(cached.DetectedLinePolynomial, fallback.DetectedLinePolynomial);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ProcessAsync_NoAvailableMemoryReported_FallsBackToDiskAndWarns()
    {
        const int width = 10;
        const int height = 8;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            WriteSyntheticFile(path, width, height, frameCount: 3, lineRow: 4, background: 500, depth: 300, sigma: 1.0);

            var processor = new ShgProcessor(() => new SerReader(), availableMemoryBytesProvider: () => 0);
            var processParams = ProcessParams.CreateDefault() with { RequestedImages = new RequestedImages([GeneratedImageKind.Raw]) };

            var messages = new List<string>();
            var progress = new Progress<string>(messages.Add);

            var result = await processor.ProcessAsync(path, processParams, progress: progress);

            Assert.Single(result.Images);
            Assert.Contains(messages, m => m.Contains("re-read it from disk", StringComparison.Ordinal));
            Assert.DoesNotContain(messages, m => m.Contains("Loading frames into memory", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertSamePixels(ushort[,] a, ushort[,] b, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Assert.Equal(a[y, x], b[y, x]);
            }
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

    [Fact]
    public async Task ProcessAsync_AutoDetectionMode_NoEquipmentSupplied_SkipsIdentificationGracefully()
    {
        // Regression guard: ProcessParams.CreateDefault()'s own DetectionMode is Auto, not Manual - a
        // caller (or existing test) that omits identificationEquipment entirely must keep getting
        // exactly the pre-existing behaviour (no identification attempted, no exception), since
        // there's nothing to compute dispersion against without it.
        const int width = 20;
        const int height = 15;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            WriteSyntheticFile(path, width, height, frameCount: 5, lineRow: 7, background: 1000, depth: 800, sigma: 1.5);

            var processor = new ShgProcessor(() => new SerReader());
            var processParams = ProcessParams.CreateDefault() with { RequestedImages = new RequestedImages([GeneratedImageKind.Raw]) };

            var result = await processor.ProcessAsync(path, processParams);

            Assert.Single(result.Images);
            Assert.Null(result.IdentifiedRay);
            Assert.Null(result.IdentificationScore);
            Assert.False(result.IdentificationConfident);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ProcessAsync_ManualDetectionMode_NeverAttemptsIdentificationEvenWithEquipmentSupplied()
    {
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            // A profile sampled from the real bundled H-alpha window - identification would confidently
            // recognise this as H-alpha if it ran at all (see the Auto-mode test below), so Manual
            // reporting no identification proves it genuinely wasn't attempted, not just that it failed.
            WriteSyntheticFileFromBundledLine(path, SpectralRay.HAlpha, width: 10, frameCount: 3);

            var processor = new ShgProcessor(() => new SerReader());
            var defaults = ProcessParams.CreateDefault();
            var processParams = defaults with
            {
                RequestedImages = new RequestedImages([GeneratedImageKind.Raw]),
                SpectrumParams = defaults.SpectrumParams with { Ray = SpectralRay.CalciumK, DetectionMode = LineDetectionMode.Manual },
            };

            var result = await processor.ProcessAsync(path, processParams, new LineIdentificationEquipment(Instrument, PixelSizeMicrons));

            Assert.Null(result.IdentifiedRay);
            Assert.Null(result.IdentificationScore);
            Assert.False(result.IdentificationConfident);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ProcessAsync_AutoDetectionMode_WithEquipmentSupplied_IdentifiesConfidentLineAndOverridesConfiguredRay()
    {
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            WriteSyntheticFileFromBundledLine(path, SpectralRay.HAlpha, width: 10, frameCount: 3);

            var processor = new ShgProcessor(() => new SerReader());
            var defaults = ProcessParams.CreateDefault();
            var processParams = defaults with
            {
                RequestedImages = new RequestedImages([GeneratedImageKind.Raw]),
                // Deliberately wrong, to prove a confident identification overrides it rather than
                // merely agreeing with whatever happened to already be configured.
                SpectrumParams = defaults.SpectrumParams with { Ray = SpectralRay.CalciumK, DetectionMode = LineDetectionMode.Auto },
            };

            var result = await processor.ProcessAsync(path, processParams, new LineIdentificationEquipment(Instrument, PixelSizeMicrons));

            Assert.Equal(SpectralRay.HAlpha, result.IdentifiedRay);
            Assert.True(result.IdentificationConfident);
            Assert.True(result.IdentificationScore > 0.9, $"Expected a strong match, got {result.IdentificationScore}.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Builds a synthetic SER file whose per-row intensity (identical across every column and
    /// frame - no curvature, no disk shape, matching <see cref="WriteSyntheticFile"/>'s own convention)
    /// is sampled from the real bundled reference window for <paramref name="ray"/> - a genuine
    /// solar-atlas-shaped profile <see cref="SpectralLineIdentifier"/> should confidently recognise,
    /// not an idealized Gaussian (same rationale as <c>SpectralLineIdentifierTests</c>' own
    /// real-bundled-window regression test).</summary>
    private static void WriteSyntheticFileFromBundledLine(string path, SpectralRay ray, int width, int frameCount)
    {
        var window = ReferenceWindowResource.LoadEmbedded().Single(w => Math.Abs(w.CenterWavelengthAngstroms - ray.WavelengthAngstroms) < 0.01);
        var dispersion = SpectralDispersion.ComputeAngstromsPerPixel(Instrument, ray.WavelengthAngstroms, PixelSizeMicrons);
        var maxShiftPixels = (int)(window.StepAngstroms * (window.Intensities.Count - 1) / 2.0 / dispersion) - 5;
        var height = (2 * maxShiftPixels) + 1;

        using var writer = new SerWriter();
        writer.Open(path, width, height, 16);
        for (var f = 0; f < frameCount; f++)
        {
            var data = new byte[width * height * 2];
            for (var y = 0; y < height; y++)
            {
                var shift = y - maxShiftPixels;
                var wavelength = window.CenterWavelengthAngstroms + (shift * dispersion);
                var value = (ushort)Math.Clamp(window.IntensityAt(wavelength) ?? 0, 0, 65535);
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
