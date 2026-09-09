using SolScan.Core.Camera;
using SolScan.Infrastructure.Camera;

namespace SolScan.Tests;

public class JsonCameraSettingsStoreTests
{
    [Fact]
    public void Load_ReturnsNull_WhenNothingSavedForThatCamera()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = new JsonCameraSettingsStore(directory);

            Assert.Null(store.Load("ZWO ASI678MM"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAndKeepsCamerasSeparate()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = new JsonCameraSettingsStore(directory);
            var asiSettings = new CameraSettings(
                Gain: 150, ExposureMicroseconds: 8_000, UsbBandwidthPercent: 80,
                IsGainAuto: false, IsExposureAuto: false, IsUsbBandwidthAuto: true,
                OutputFormat: CameraOutputFormat.Mono16, Binning: 2,
                ContrastBlackPoint: 0.1, ContrastWhitePoint: 0.9, IsContrastAuto: false);
            var altairSettings = asiSettings with { Gain = 42, Binning = 1 };

            store.Save("ZWO ASI678MM", asiSettings);
            store.Save("Altair GPCAM3", altairSettings);

            // A second store instance pointed at the same directory - proves this round-trips
            // through the actual JSON file, not just an in-memory cache.
            var reloaded = new JsonCameraSettingsStore(directory);
            Assert.Equal(asiSettings, reloaded.Load("ZWO ASI678MM"));
            Assert.Equal(altairSettings, reloaded.Load("Altair GPCAM3"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Save_OverwritesPreviousSettingsForTheSameCamera()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = new JsonCameraSettingsStore(directory);
            var original = new CameraSettings(150, 8_000, 80, false, false, false, CameraOutputFormat.Mono16, 1, 0, 1, false);
            var updated = original with { Gain = 300, Binning = 4 };

            store.Save("ZWO ASI678MM", original);
            store.Save("ZWO ASI678MM", updated);

            Assert.Equal(updated, store.Load("ZWO ASI678MM"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"solscan-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
