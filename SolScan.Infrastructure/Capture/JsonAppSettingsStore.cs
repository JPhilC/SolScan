using System.Text.Json;
using SolScan.Core.Capture;

namespace SolScan.Infrastructure.Capture;

/// <summary>
/// JSON-file-backed <see cref="IAppSettingsStore"/> - one file under
/// %LocalAppData%\SolScan\app-settings.json, mirroring
/// SolScan.Infrastructure.Camera.JsonCameraSettingsStore's own shape/rationale: per-machine data,
/// not something that should roam to another machine.
/// </summary>
public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _file;

    public JsonAppSettingsStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolScan"))
    {
    }

    public JsonAppSettingsStore(string storageDirectory)
    {
        Directory.CreateDirectory(storageDirectory);
        _file = Path.Combine(storageDirectory, "app-settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_file))
        {
            return new AppSettings(null);
        }

        try
        {
            using var stream = File.OpenRead(_file);
            return JsonSerializer.Deserialize<AppSettings>(stream, SerializerOptions) ?? new AppSettings(null);
        }
        catch (JsonException)
        {
            // Corrupt/unreadable file - fall back to "nothing saved" rather than crashing.
            return new AppSettings(null);
        }
    }

    public void Save(AppSettings settings)
    {
        using var stream = File.Create(_file);
        JsonSerializer.Serialize(stream, settings, SerializerOptions);
    }
}
