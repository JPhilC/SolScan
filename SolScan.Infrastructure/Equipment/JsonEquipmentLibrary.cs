using System.Text.Json;
using SolScan.Core.Equipment;

namespace SolScan.Infrastructure.Equipment;

/// <summary>
/// JSON-file-backed IEquipmentLibrary, one file per library under
/// %LocalAppData%\SolScan\equipment\ - same shape as astro4j's SpectroHeliographsIO/SetupsIO (a
/// single JSON array per file, seeded with SpectrographProfile.Predefined() the first time nothing
/// exists on disk - telescopes/cameras/setups have no equivalent universal preset, so they start
/// empty), but using System.Text.Json rather than Gson since this is a from-scratch C# port, not a
/// line-for-line translation. Local rather than Roaming: this is per-machine hardware (an SHG,
/// telescope, camera physically attached to this PC), not user preference data that should follow
/// the user to another machine.
/// </summary>
public sealed class JsonEquipmentLibrary : IEquipmentLibrary
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _spectrographsFile;
    private readonly string _telescopesFile;
    private readonly string _camerasFile;
    private readonly string _setupsFile;

    public JsonEquipmentLibrary()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SolScan",
            "equipment"))
    {
    }

    public JsonEquipmentLibrary(string storageDirectory)
    {
        Directory.CreateDirectory(storageDirectory);
        _spectrographsFile = Path.Combine(storageDirectory, "spectrographs.json");
        _telescopesFile = Path.Combine(storageDirectory, "telescopes.json");
        _camerasFile = Path.Combine(storageDirectory, "cameras.json");
        _setupsFile = Path.Combine(storageDirectory, "setups.json");
    }

    public IReadOnlyList<SpectrographProfile> LoadSpectrographs() =>
        ReadFrom<SpectrographProfile>(_spectrographsFile) ?? SpectrographProfile.Predefined();

    public void SaveSpectrographs(IReadOnlyList<SpectrographProfile> profiles) =>
        WriteTo(_spectrographsFile, profiles);

    public IReadOnlyList<TelescopeProfile> LoadTelescopes() =>
        ReadFrom<TelescopeProfile>(_telescopesFile) ?? [];

    public void SaveTelescopes(IReadOnlyList<TelescopeProfile> profiles) =>
        WriteTo(_telescopesFile, profiles);

    public IReadOnlyList<CameraProfile> LoadCameras() =>
        ReadFrom<CameraProfile>(_camerasFile) ?? [];

    public void SaveCameras(IReadOnlyList<CameraProfile> profiles) =>
        WriteTo(_camerasFile, profiles);

    public IReadOnlyList<EquipmentSetup> LoadSetups() =>
        ReadFrom<EquipmentSetup>(_setupsFile) ?? [];

    public void SaveSetups(IReadOnlyList<EquipmentSetup> setups) =>
        WriteTo(_setupsFile, setups);

    private static List<T>? ReadFrom<T>(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(file);
            return JsonSerializer.Deserialize<List<T>>(stream, SerializerOptions);
        }
        catch (JsonException)
        {
            // Corrupt/unreadable file - fall back to defaults rather than crashing the Options view.
            return null;
        }
    }

    private static void WriteTo<T>(string file, IReadOnlyList<T> items)
    {
        using var stream = File.Create(file);
        JsonSerializer.Serialize(stream, items, SerializerOptions);
    }
}
