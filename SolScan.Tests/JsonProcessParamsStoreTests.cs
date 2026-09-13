using System.Text.Json;
using System.Text.Json.Nodes;
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
                ContrastEnhancementMode.Clahe,
                new ClaheParams(16, 128, 1.2),
                new Clahe2Params(2.0),
                new AutoStretchParams(1.8, 0.4, 0.5));

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
    public void Load_BackfillsContrastTuningDefaults_WhenLoadingAnOlderFileThatPredatesThem()
    {
        var directory = CreateTempDirectory();
        try
        {
            // Simulates a process-params.json written before ClaheParams/Clahe2Params/AutoStretchParams
            // existed: serialize a real ProcessParams, then strip those three properties back out,
            // rather than hand-typing brittle nested JSON that could drift from the real record shapes.
            var full = JsonSerializer.SerializeToNode(ProcessParams.CreateDefault())!.AsObject();
            full.Remove(nameof(ProcessParams.ClaheParams));
            full.Remove(nameof(ProcessParams.Clahe2Params));
            full.Remove(nameof(ProcessParams.AutoStretchParams));
            File.WriteAllText(Path.Combine(directory, "process-params.json"), full.ToJsonString());

            var store = new JsonProcessParamsStore(directory);
            var loaded = store.Load();

            Assert.Equal(ClaheParams.Default, loaded.ClaheParams);
            Assert.Equal(Clahe2Params.Default, loaded.Clahe2Params);
            Assert.Equal(AutoStretchParams.Default, loaded.AutoStretchParams);
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
