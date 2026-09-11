namespace SolScan.Core.Equipment;

/// <summary>
/// A telescope's own optical parameters - independent of whatever camera happens to be attached to
/// it for a given session (see <see cref="CameraProfile"/> for that side, split out from what used
/// to be one combined "EquipmentProfile" - the camera is no longer picked from a library at all, see
/// CaptureViewModel's camera auto-add), and independent of the mount it rides on (see
/// SolScan.Core.Telescope.ITelescopeMount - a live connection, not a library entry) and of site
/// geometry (now owned by <see cref="Capture.AppSettings"/>/Telescope.MountState). See
/// <see cref="EquipmentSetup"/> for a saved SHG+telescope combination.
///
/// Partial translation of astro4j's Setup.java (jsolex-core, Apache-2.0) - see NOTICE - scoped down
/// to just the fields it shares with this record (now including <see cref="EnergyRejectionFilter"/>,
/// matching Setup.java's own <c>energyRejectionFilter</c> field); Setup.java's camera/mount/site
/// fields live elsewhere in SolScan now (see above), not here.
/// </summary>
public sealed record TelescopeProfile(
    Guid Id,
    string Label,
    double? FocalLengthMm,
    double? ApertureMm,
    string? EnergyRejectionFilter)
{
    public static TelescopeProfile CreateDefault(string label = "My telescope") =>
        new(Guid.NewGuid(), label, null, null, null);
}
