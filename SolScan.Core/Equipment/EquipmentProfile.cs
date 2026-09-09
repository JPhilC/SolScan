namespace SolScan.Core.Equipment;

/// <summary>
/// A telescope+camera+mount combination - independent of which SHG happens to be attached to it
/// for a given session. See <see cref="SpectrographProfile"/> for that side, and
/// <see cref="EquipmentSetup"/> for a saved SHG+equipment combination.
///
/// Close translation of astro4j's Setup.java (jsolex-core, Apache-2.0) - see NOTICE. Scoped to
/// equatorial mounts only for v1 (SolScan CLAUDE.md "Scope for v1"), so - unlike Setup.java -
/// there is no AltAz mode flag here, and site lat/long live here rather than being duplicated
/// per-EquipmentSetup, matching Setup.java's own shape.
/// </summary>
public sealed record EquipmentProfile(
    Guid Id,
    string Label,
    string? Telescope,
    double? TelescopeFocalLengthMm,
    double? ApertureMm,
    string? Camera,
    double? CameraPixelSizeMicrons,
    string? Mount,
    double? SiteLatitudeDeg,
    double? SiteLongitudeDeg)
{
    public static EquipmentProfile CreateDefault(string label = "My setup") =>
        new(Guid.NewGuid(), label, null, null, null, null, null, null, null, null);
}
