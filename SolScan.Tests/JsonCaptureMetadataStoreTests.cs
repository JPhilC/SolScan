using SolScan.Core.Camera;
using SolScan.Core.Capture;
using SolScan.Core.Equipment;
using SolScan.Core.Processing;
using SolScan.Infrastructure.Capture;

namespace SolScan.Tests;

public class JsonCaptureMetadataStoreTests
{
    [Fact]
    public void WriteThenTryRead_RoundTrips()
    {
        var serPath = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");
        var metadata = new CaptureMetadata(
            new SpectrographProfile(Guid.NewGuid(), "Sunscan Crompton Variant", 34, 90, 75, 2400, 1, 10, 6, true),
            new TelescopeProfile(Guid.NewGuid(), "50mm (Sunscan)", 120, 25, "0.8"),
            new CameraProfile(Guid.NewGuid(), "IMX477", 1.55),
            DateTime.UtcNow);
        var sidecarPath = Path.ChangeExtension(serPath, ".equipment.json");

        try
        {
            var store = new JsonCaptureMetadataStore();

            store.Write(serPath, metadata);

            Assert.Equal(metadata, store.TryRead(serPath));
        }
        finally
        {
            File.Delete(sidecarPath);
        }
    }

    [Fact]
    public void WriteThenTryRead_RoundTripsCameraSettingsAndMountPointing()
    {
        var serPath = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");
        var metadata = new CaptureMetadata(
            Spectrograph: null,
            Telescope: null,
            Camera: null,
            CapturedAtUtc: DateTime.UtcNow,
            CameraSettingsUsed: new CameraSettings(150, 8_000, 80, false, false, true, CameraOutputFormat.Mono16, 2, 0.1, 0.9, false, 3840, 500),
            MountPointing: new MountPointingSnapshot(12.34, -5.67, 51.5, -0.1, 50),
            StudiedRay: SpectralRay.HAlpha);
        var sidecarPath = Path.ChangeExtension(serPath, ".equipment.json");

        try
        {
            var store = new JsonCaptureMetadataStore();

            store.Write(serPath, metadata);

            Assert.Equal(metadata, store.TryRead(serPath));
        }
        finally
        {
            File.Delete(sidecarPath);
        }
    }

    [Fact]
    public void TryRead_OlderSidecarWithoutTheNewerFields_ReadsBackWithThemNull()
    {
        // Simulates a sidecar written before CaptureMetadata grew CameraSettingsUsed/MountPointing/
        // StudiedRay - the four original fields only.
        var serPath = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");
        var sidecarPath = Path.ChangeExtension(serPath, ".equipment.json");
        File.WriteAllText(sidecarPath, """
            {
              "Spectrograph": null,
              "Telescope": null,
              "Camera": null,
              "CapturedAtUtc": "2025-08-31T16:14:27Z"
            }
            """);

        try
        {
            var metadata = new JsonCaptureMetadataStore().TryRead(serPath);

            Assert.NotNull(metadata);
            Assert.Null(metadata.CameraSettingsUsed);
            Assert.Null(metadata.MountPointing);
            Assert.Null(metadata.StudiedRay);
        }
        finally
        {
            File.Delete(sidecarPath);
        }
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenNoSidecarExists()
    {
        var serPath = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");

        Assert.Null(new JsonCaptureMetadataStore().TryRead(serPath));
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenSidecarIsNotValidJson()
    {
        var serPath = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}.ser");
        var sidecarPath = Path.ChangeExtension(serPath, ".equipment.json");
        File.WriteAllText(sidecarPath, "not valid json");

        try
        {
            Assert.Null(new JsonCaptureMetadataStore().TryRead(serPath));
        }
        finally
        {
            File.Delete(sidecarPath);
        }
    }
}
