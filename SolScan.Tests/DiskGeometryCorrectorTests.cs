using SolScan.Core.Camera;
using SolScan.Core.Processing;
using SolScan.Infrastructure.Capture;
using SolScan.Processing.Shg;

namespace SolScan.Tests;

public class DiskGeometryCorrectorTests
{
    [Fact]
    public async Task ProcessAsync_GeometryCorrected_FitsAndCorrectsASyntheticEllipticalDisk()
    {
        // A 72x64 reconstructed image (64 SER frames, each 72 columns wide) whose brightness is
        // shaped like a centred, axis-aligned ellipse (deliberately not a circle - a circle's tilt
        // angle is ill-conditioned/arbitrary, since it has no unique major axis) - bright "on disk",
        // dim "off disk" - with a flat spectral line at the same row in every frame/column, so the
        // line-curvature fit is trivial and the only interesting thing under test is disk-edge
        // detection/geometry correction.
        const int width = 72;
        const int frameCount = 64;
        const int height = 24;
        const int lineRow = 12;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            WriteSyntheticDiskFile(path, width, height, frameCount, lineRow);

            var processor = new ShgProcessor(() => new SerReader());
            var processParams = ProcessParams.CreateDefault() with
            {
                RequestedImages = new RequestedImages([GeneratedImageKind.GeometryCorrected]),
            };

            var result = await processor.ProcessAsync(path, processParams);

            var geometryCorrected = Assert.Single(result.Images, i => i.Kind == GeneratedImageKind.GeometryCorrected);
            Assert.True(geometryCorrected.Width > 0);
            Assert.True(geometryCorrected.Height > 0);

            // The synthetic ellipse is axis-aligned (rx=28, ry=20 - see WriteSyntheticDiskFile, a
            // 1.4:1 aspect ratio) with no tilt, so it should come back near-zero tilt and a
            // correspondingly off-1 X/Y ratio (astro4j's own detectedRatio couples the semi-axis
            // lengths with the tilt angle - see GeometryTransform.of - so its sign convention isn't
            // simply "shorter over longer"; what matters here is that a real, non-trivial ratio was
            // detected in the right ballpark, not its exact direction) - loose tolerances since the
            // pipeline (blur/background neutralization/threshold scan) is genuinely approximate, not
            // an exact geometric reconstruction.
            Assert.True(result.DetectedTiltDegrees.HasValue);
            Assert.InRange(Math.Abs(result.DetectedTiltDegrees!.Value), 0, 10);
            Assert.True(result.DetectedXyRatio.HasValue);
            var ratio = result.DetectedXyRatio!.Value;
            var normalizedRatio = ratio < 1 ? 1 / ratio : ratio;
            Assert.InRange(normalizedRatio, 1.1, 1.7);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ProcessAsync_GeometryCorrected_LowDynamicRangeCapture_StillProducesARealDiskNotABlankOne()
    {
        // Regression test for a real bug found against an actual (low-gain) Sunscan capture: its raw
        // reconstruction's native pixel values occupied well under 10% of the full 16-bit range
        // (roughly 900-3500 out of 65535). DiskEdgeDetector's background-neutralization loop only
        // stretched the image to fill that range *after* neutralizing, not before - and
        // EstimateBackgroundLevel's histogram always bins over the full [0, maxPixelValue] range
        // regardless of what part of it the data occupies, so with the signal crammed into the
        // histogram's first few buckets it produced a wildly overestimated background level. The
        // neutralization loop then kept subtracting a shrinking-but-still-substantial fraction of the
        // *signal itself* every iteration - never converging within its 2% threshold - and wiped out
        // most of the image over the full 16-iteration cap (confirmed: large regions came back exactly
        // zero). Reproduced here with a synthetic disk confined to a similarly narrow band
        // (skyLevel=900, diskLevel=1200 out of 65535, vs. the other tests' skyLevel=2000/diskLevel=40000).
        const int width = 72;
        const int frameCount = 64;
        const int height = 24;
        const int lineRow = 12;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            WriteSyntheticDiskFile(path, width, height, frameCount, lineRow, diskLevel: 1200, skyLevel: 900, depth: 100);

            var processor = new ShgProcessor(() => new SerReader());
            var processParams = ProcessParams.CreateDefault() with
            {
                RequestedImages = new RequestedImages([GeneratedImageKind.GeometryCorrected]),
            };

            var result = await processor.ProcessAsync(path, processParams);

            var geometryCorrected = Assert.Single(result.Images, i => i.Kind == GeneratedImageKind.GeometryCorrected);
            Assert.True(geometryCorrected.Width > 0);
            Assert.True(geometryCorrected.Height > 0);

            // The bug produced an image that was overwhelmingly (not just partly) zeroed out. A
            // healthy corrected image, scaled to fill the 16-bit output container, should have most of
            // its pixels well above zero even accounting for real black borders/corners from the warp.
            var nonZeroCount = 0;
            var total = geometryCorrected.Width * geometryCorrected.Height;
            for (var y = 0; y < geometryCorrected.Height; y++)
            {
                for (var x = 0; x < geometryCorrected.Width; x++)
                {
                    if (geometryCorrected.Pixels[y, x] > 0)
                    {
                        nonZeroCount++;
                    }
                }
            }

            Assert.True(nonZeroCount > total / 4, $"Expected most of a {geometryCorrected.Width}x{geometryCorrected.Height} corrected disk image to be non-zero, but only {nonZeroCount}/{total} pixels were.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ProcessAsync_GeometryCorrectedProcessed_IsStillReportedAsSkipped()
    {
        const int width = 72;
        const int frameCount = 64;
        const int height = 24;
        const int lineRow = 12;
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        try
        {
            WriteSyntheticDiskFile(path, width, height, frameCount, lineRow);

            var processor = new ShgProcessor(() => new SerReader());
            var processParams = ProcessParams.CreateDefault() with
            {
                RequestedImages = new RequestedImages([GeneratedImageKind.GeometryCorrectedProcessed]),
            };

            var result = await processor.ProcessAsync(path, processParams);

            Assert.Empty(result.Images);
            var skipped = Assert.Single(result.SkippedKinds);
            Assert.Equal(GeneratedImageKind.GeometryCorrectedProcessed, skipped);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Writes a SER file whose reconstructed disk image (stacking the row near
    /// <paramref name="lineRow"/> from every frame) comes out as a bright, axis-aligned ellipse on a
    /// dim background (semi-axes 28 columns x 20 frames): each frame/column's absorption-line depth
    /// sits on top of a background level that's high inside the ellipse and low outside it.</summary>
    private static void WriteSyntheticDiskFile(string path, int width, int height, int frameCount, int lineRow, double diskLevel = 40000, double skyLevel = 2000, double depth = 1500)
    {
        const double sigma = 2.0;
        const double rx = 28;
        const double ry = 20;
        var cx = (width - 1) / 2.0;
        var cf = (frameCount - 1) / 2.0;

        using var writer = new SerWriter();
        writer.Open(path, width, height, 16);
        for (var f = 0; f < frameCount; f++)
        {
            var data = new byte[width * height * 2];
            for (var x = 0; x < width; x++)
            {
                var dx = (x - cx) / rx;
                var df = (f - cf) / ry;
                var insideDisk = (dx * dx) + (df * df) <= 1;
                var background = insideDisk ? diskLevel : skyLevel;

                for (var y = 0; y < height; y++)
                {
                    var dy = y - lineRow;
                    var value = (ushort)Math.Clamp(background - (depth * Math.Exp(-(dy * dy) / (2 * sigma * sigma))), 0, 65535);
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
