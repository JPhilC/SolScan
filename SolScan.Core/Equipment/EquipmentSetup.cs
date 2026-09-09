namespace SolScan.Core.Equipment;

/// <summary>
/// A saved "this SHG is mounted behind this telescope/camera" combination - the library entry the
/// user actually picks from in Prepare/Capture, so they don't have to reselect a spectrograph and
/// an equipment profile separately every session.
///
/// New to SolScan, not a port: astro4j/JSolex keeps SpectroHeliograph and Setup as two
/// independently-selected libraries with no persisted combination between them (see
/// SpectroHeliographEditor.java / SetupEditor.java - each only ever edits its own list). SolScan
/// adds this third library on top of the same two building blocks, referencing them by
/// <see cref="SpectrographProfile.Id"/>/<see cref="EquipmentProfile.Id"/> rather than embedding
/// copies, so editing a spectrograph or equipment profile updates every setup that uses it.
/// </summary>
public sealed record EquipmentSetup(
    Guid Id,
    string Label,
    Guid SpectrographProfileId,
    Guid EquipmentProfileId)
{
    public static EquipmentSetup Create(string label, Guid spectrographProfileId, Guid equipmentProfileId) =>
        new(Guid.NewGuid(), label, spectrographProfileId, equipmentProfileId);
}
