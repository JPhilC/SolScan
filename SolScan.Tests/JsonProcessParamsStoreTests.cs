using SolScan.Core.Processing;
using SolScan.Infrastructure.Processing;

namespace SolScan.Tests;

public class JsonProcessParamsStoreTests
{
    [Fact]
    public void Load_ReturnsDefaults_WhenNothingSavedYet()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = new JsonProcessParamsStore(directory);

            Assert.Equal(ProcessParams.CreateDefault(), store.Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = new JsonProcessParamsStore(directory);
            var processParams = new ProcessParams(
                new RequestedImages([GeneratedImageKind.Raw, GeneratedImageKind.GeometryCorrected]),
                new SpectrumParams(SpectralRay.CalciumK, LineDetectionMode.Manual, 1.5, 3, 15, true),
                new GeometryParams(RotationKind.Left, AutocropMode.Radius1To2, 1900, true, false),
                ContrastEnhancementMode.Clahe);

            store.Save(processParams);

            // A second store instance pointed at the same directory - proves this round-trips
            // through the actual JSON file, not just an in-memory cache.
            var reloaded = new JsonProcessParamsStore(directory);
            Assert.Equal(processParams, reloaded.Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenFileIsCorrupt()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "process-params.json"), "not valid json");

            var store = new JsonProcessParamsStore(directory);

            Assert.Equal(ProcessParams.CreateDefault(), store.Load());
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
