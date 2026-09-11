using SolScan.Core.Camera;
using SolScan.Core.Equipment;
using SolScan.Core.Processing;

namespace SolScan.Core.Capture;

/// <summary>
/// Everything about one recording that a future processing pipeline needs but the .ser file itself
/// doesn't carry - real field values, not IDs/live references, so it survives the source data (an
/// <see cref="IEquipmentLibrary"/> entry, a live <see cref="SolScan.Core.Telescope.MountState"/>
/// reading) later changing or going away. Originally just the SHG/telescope/camera used (hence the
/// name this type used to have, <c>CaptureEquipmentMetadata</c>) - broadened to also snapshot the camera dial-in
/// settings and mount pointing actually in effect for this specific recording, neither of which was
/// captured per-file anywhere before (<see cref="ICameraSettingsStore"/> only remembers a camera
/// model's *current* settings, not what a past recording used). Every field can end up null - e.g. no
/// Equipment Setup was selected on Prepare, no camera has connected yet, or the mount wasn't
/// connected when recording started - written anyway, rather than skipped, so a recording still gets
/// a metadata file either way.
/// </summary>
/// <param name="StudiedRay">Which spectral line was being studied - not yet populated by anything
/// (always null today). Once Capture's live line-identification overlay exists (CLAUDE.md Phase 4)
/// this should be set from whichever labeled line sits nearest the ROI's vertical centre, rather than
/// only ever being set by hand in Options > Process Parameters as it is today.</param>
public sealed record CaptureMetadata(
    SpectrographProfile? Spectrograph,
    TelescopeProfile? Telescope,
    CameraProfile? Camera,
    DateTime CapturedAtUtc,
    CameraSettings? CameraSettingsUsed = null,
    MountPointingSnapshot? MountPointing = null,
    SpectralRay? StudiedRay = null);

/// <summary>The mount's RA/Dec and site location at the moment a recording started - a snapshot of
/// <see cref="SolScan.Core.Telescope.MountState"/> (already poll-refreshed roughly once a second, see
/// SolScan.App.Services.MountService), not a fresh Alpaca query, so capturing it at recording-start
/// time costs nothing extra. Absent (null on the parent <see cref="CaptureMetadata"/>) when the mount
/// wasn't connected at that moment.</summary>
public sealed record MountPointingSnapshot(
    double RightAscensionHours,
    double DeclinationDeg,
    double SiteLatitudeDeg,
    double SiteLongitudeDeg,
    double SiteElevationM);

/// <summary>
/// Persists <see cref="CaptureMetadata"/> alongside a .ser recording - implemented by
/// SolScan.Infrastructure.Capture.JsonCaptureMetadataStore. Named for what it does (store: both
/// directions), not just <c>...Writer</c> as it originally was, since it's always supported reading
/// back too (see <see cref="TryRead"/>) - this type just picked up the name mismatch as an oversight
/// at the time.
/// </summary>
public interface ICaptureMetadataStore
{
    /// <param name="serFilePath">The .ser recording's own path - the metadata is written alongside
    /// it (same base name, ".equipment.json" extension), not inside the SER file itself.</param>
    void Write(string serFilePath, CaptureMetadata metadata);

    /// <summary>Reads back the sidecar <see cref="Write"/> wrote for a given .ser file. Never throws:
    /// returns null if no sidecar exists yet (e.g. the .ser file wasn't captured by SolScan) or it's
    /// present but unreadable/corrupt - same never-throws-on-missing/corrupt convention as
    /// <see cref="IAppSettingsStore.Load"/>. A sidecar written before <see cref="CaptureMetadata"/>
    /// grew its extra fields still reads back fine - they just come back null, same as any other
    /// field this recording never had a value for.</summary>
    /// <param name="serFilePath">The .ser recording's own path - same sidecar-naming convention as
    /// <see cref="Write"/>.</param>
    CaptureMetadata? TryRead(string serFilePath);
}
