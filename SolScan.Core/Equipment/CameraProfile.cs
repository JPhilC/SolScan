namespace SolScan.Core.Equipment;

/// <summary>
/// A camera model's own characteristics - split out from what used to be one combined
/// "EquipmentProfile" (see <see cref="TelescopeProfile"/> for that side). Unlike telescopes/SHGs,
/// cameras aren't picked from this library by hand in the normal flow: SolScan.App's
/// CaptureViewModel auto-adds an entry the first time a given camera model connects (matched by
/// <see cref="Label"/> against <see cref="Camera.ICameraDevice.Name"/> - the same "key by Name, not
/// Id" reasoning as <see cref="Camera.ICameraSettingsStore"/>'s own doc comment: a device's Id is a
/// discovery-session-local index, not stable across sessions), filling in
/// <see cref="PixelSizeMicrons"/> by querying the connected hardware. The Options > Cameras tab still
/// lets a camera be added/edited by hand too, same as SHGs/telescopes.
/// </summary>
public sealed record CameraProfile(
    Guid Id,
    string Label,
    double? PixelSizeMicrons)
{
    public static CameraProfile CreateDefault(string label = "My camera") =>
        new(Guid.NewGuid(), label, null);
}
