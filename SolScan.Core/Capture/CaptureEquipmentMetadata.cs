using SolScan.Core.Equipment;

namespace SolScan.Core.Capture;

/// <summary>
/// Snapshot of the SHG/telescope/camera used to produce one recording - real field values, not IDs,
/// so SolScan.Processing (a future phase - see SolScan CLAUDE.md) can read back what equipment
/// produced a given recording independent of whether the <see cref="IEquipmentLibrary"/> entries it
/// was snapshotted from are later edited or deleted in Options. Any of the three can be null - e.g.
/// no Equipment Setup was selected on Prepare, or no camera has connected yet.
/// </summary>
public sealed record CaptureEquipmentMetadata(
    SpectrographProfile? Spectrograph,
    TelescopeProfile? Telescope,
    CameraProfile? Camera,
    DateTime CapturedAtUtc);

/// <summary>
/// Persists <see cref="CaptureEquipmentMetadata"/> alongside a .ser recording - implemented by
/// SolScan.Infrastructure.Capture.JsonCaptureMetadataWriter.
/// </summary>
public interface ICaptureMetadataWriter
{
    /// <param name="serFilePath">The .ser recording's own path - the metadata is written alongside
    /// it (same base name, ".equipment.json" extension), not inside the SER file itself.</param>
    void Write(string serFilePath, CaptureEquipmentMetadata metadata);
}
