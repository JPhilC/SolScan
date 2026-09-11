using System.Text.Json;
using SolScan.Core.Processing;

namespace SolScan.Infrastructure.Processing;

/// <summary>
/// JSON-file-backed <see cref="IProcessParamsStore"/> - one file under
/// %LocalAppData%\SolScan\process-params.json, same shape/rationale as
/// SolScan.Infrastructure.Capture.JsonAppSettingsStore.
/// </summary>
public sealed class JsonProcessParamsStore : IProcessParamsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _file;

    public JsonProcessParamsStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolScan"))
    {
    }

    public JsonProcessParamsStore(string storageDirectory)
    {
        Directory.CreateDirectory(storageDirectory);
        _file = Path.Combine(storageDirectory, "process-params.json");
    }

    public ProcessParams Load()
    {
        if (!File.Exists(_file))
        {
            return ProcessParams.CreateDefault();
        }

        try
        {
            using var stream = File.OpenRead(_file);
            return JsonSerializer.Deserialize<ProcessParams>(stream, SerializerOptions) ?? ProcessParams.CreateDefault();
        }
        catch (JsonException)
        {
            // Corrupt/unreadable file - fall back to defaults rather than crashing.
            return ProcessParams.CreateDefault();
        }
    }

    public void Save(ProcessParams processParams)
    {
        using var stream = File.Create(_file);
        JsonSerializer.Serialize(stream, processParams, SerializerOptions);
    }
}
