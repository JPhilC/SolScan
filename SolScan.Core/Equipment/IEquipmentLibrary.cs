namespace SolScan.Core.Equipment;

/// <summary>
/// Persists the three equipment libraries (SHGs, telescope/camera profiles, and saved SHG+
/// equipment combinations) that back the Options view - SolScan's equivalent of JSolex's
/// "Equipment" menu (SpectroHeliographEditor/SetupEditor), plus the SolScan-specific
/// EquipmentSetup combination library. Implemented in SolScan.Infrastructure (JSON-file-backed,
/// mirroring astro4j's SpectroHeliographsIO/SetupsIO) so SolScan.Core stays free of concrete IO.
/// </summary>
public interface IEquipmentLibrary
{
    IReadOnlyList<SpectrographProfile> LoadSpectrographs();
    void SaveSpectrographs(IReadOnlyList<SpectrographProfile> profiles);

    IReadOnlyList<EquipmentProfile> LoadEquipmentProfiles();
    void SaveEquipmentProfiles(IReadOnlyList<EquipmentProfile> profiles);

    IReadOnlyList<EquipmentSetup> LoadSetups();
    void SaveSetups(IReadOnlyList<EquipmentSetup> setups);
}
