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
            var loaded = JsonSerializer.Deserialize<ProcessParams>(stream, SerializerOptions) ?? ProcessParams.CreateDefault();
            return BackfillMissingDefaults(loaded);
        }
        catch (JsonException)
        {
            // Corrupt/unreadable file - fall back to defaults rather than crashing.
            return ProcessParams.CreateDefault();
        }
    }

    /// <summary>A file written before <see cref="ProcessParams.ClaheParams"/>/<see cref="ProcessParams.Clahe2Params"/>/
    /// <see cref="ProcessParams.AutoStretchParams"/> existed deserializes those three properties as
    /// <see langword="null"/>, despite their non-nullable declared types - <c>System.Text.Json</c> hands
    /// a missing constructor parameter the CLR default regardless of C# nullability annotations, and
    /// (unlike <see cref="Capture.AppSettings"/>'s primitive fields) a nested record's default can't be
    /// a compile-time-constant optional-parameter default. Backfill each with its own real default
    /// rather than letting a null through to code that assumes these are always populated.</summary>
    private static ProcessParams BackfillMissingDefaults(ProcessParams loaded) => loaded with
    {
        ClaheParams = loaded.ClaheParams ?? ClaheParams.Default,
        Clahe2Params = loaded.Clahe2Params ?? Clahe2Params.Default,
        AutoStretchParams = loaded.AutoStretchParams ?? AutoStretchParams.Default,
    };

    public void Save(ProcessParams processParams)
    {
        using var stream = File.Create(_file);
        JsonSerializer.Serialize(stream, processParams, SerializerOptions);
    }
}
