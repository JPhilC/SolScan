using System.Text.Json;
using SolScan.Core.Capture;

namespace SolScan.Infrastructure.Capture;

/// <summary>
/// JSON-file-backed <see cref="ICaptureMetadataStore"/> - one file per recording, same base name as
/// the .ser file with a ".equipment.json" extension, same one-JSON-file pattern as every other
/// <c>Json*</c> store/writer in this codebase.
/// </summary>
public sealed class JsonCaptureMetadataStore : ICaptureMetadataStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public void Write(string serFilePath, CaptureMetadata metadata)
    {
        var path = Path.ChangeExtension(serFilePath, ".equipment.json");
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, metadata, SerializerOptions);
    }

    public CaptureMetadata? TryRead(string serFilePath)
    {
        var path = Path.ChangeExtension(serFilePath, ".equipment.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<CaptureMetadata>(stream, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
