using System.Text.Json;
using SolScan.Core.Camera;

namespace SolScan.Infrastructure.Camera;

/// <summary>
/// JSON-file-backed <see cref="ICameraSettingsStore"/> - one file (a camera-name -> CameraSettings
/// dictionary) under %LocalAppData%\SolScan\camera-settings.json, same shape/rationale as
/// SolScan.Infrastructure.Equipment.JsonEquipmentLibrary: per-machine data (a camera physically
/// attached to this PC), not something that should roam to another machine.
/// </summary>
public sealed class JsonCameraSettingsStore : ICameraSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _file;

    public JsonCameraSettingsStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolScan"))
    {
    }

    public JsonCameraSettingsStore(string storageDirectory)
    {
        Directory.CreateDirectory(storageDirectory);
        _file = Path.Combine(storageDirectory, "camera-settings.json");
    }

    public CameraSettings? Load(string cameraName) => ReadAll().GetValueOrDefault(cameraName);

    public void Save(string cameraName, CameraSettings settings)
    {
        var all = ReadAll();
        all[cameraName] = settings;

        using var stream = File.Create(_file);
        JsonSerializer.Serialize(stream, all, SerializerOptions);
    }

    private Dictionary<string, CameraSettings> ReadAll()
    {
        if (!File.Exists(_file))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(_file);
            return JsonSerializer.Deserialize<Dictionary<string, CameraSettings>>(stream, SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            // Corrupt/unreadable file - fall back to "nothing saved" rather than crashing Capture.
            return [];
        }
    }
}
