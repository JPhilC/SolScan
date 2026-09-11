using System.Text.Json;
using SolScan.Core.Capture;

namespace SolScan.Infrastructure.Capture;

/// <summary>
/// JSON-file-backed <see cref="ICaptureMetadataWriter"/> - one file per recording, same base name as
/// the .ser file with a ".equipment.json" extension, same one-JSON-file pattern as every other
/// <c>Json*</c> store/writer in this codebase.
/// </summary>
public sealed class JsonCaptureMetadataWriter : ICaptureMetadataWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public void Write(string serFilePath, CaptureEquipmentMetadata metadata)
    {
        var path = Path.ChangeExtension(serFilePath, ".equipment.json");
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, metadata, SerializerOptions);
    }
}
