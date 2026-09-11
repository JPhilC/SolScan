namespace SolScan.Core.Equipment;

/// <summary>
/// A saved "this SHG is mounted on this telescope" combination - the library entry the user
/// actually picks from on Prepare, so they don't have to reselect a spectrograph and a telescope
/// separately every session. No camera reference: unlike the SHG/telescope, the camera isn't picked
/// from a library at all - it's whatever's actually connected in the Capture view (see
/// CaptureViewModel's camera auto-add), so it can't meaningfully be part of a saved combo the way
/// the other two can.
///
/// New to SolScan, not a port: astro4j/JSolex keeps SpectroHeliograph and Setup as two
/// independently-selected libraries with no persisted combination between them (see
/// SpectroHeliographEditor.java / SetupEditor.java - each only ever edits its own list). SolScan
/// adds this third library on top of the same two building blocks, referencing them by
/// <see cref="SpectrographProfile.Id"/>/<see cref="TelescopeProfile.Id"/> rather than embedding
/// copies, so editing a spectrograph or telescope updates every setup that uses it.
/// </summary>
public sealed record EquipmentSetup(
    Guid Id,
    string Label,
    Guid SpectrographProfileId,
    Guid TelescopeProfileId)
{
    public static EquipmentSetup Create(string label, Guid spectrographProfileId, Guid telescopeProfileId) =>
        new(Guid.NewGuid(), label, spectrographProfileId, telescopeProfileId);
}
