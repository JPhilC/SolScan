namespace SolScan.Core.Equipment;

/// <summary>
/// Persists the four equipment libraries (SHGs, telescopes, cameras, and saved SHG+telescope
/// combinations) that back the Options view - SolScan's equivalent of JSolex's "Equipment" menu
/// (SpectroHeliographEditor/SetupEditor), plus the SolScan-specific EquipmentSetup combination
/// library. Implemented in SolScan.Infrastructure (JSON-file-backed, mirroring astro4j's
/// SpectroHeliographsIO/SetupsIO) so SolScan.Core stays free of concrete IO.
/// </summary>
public interface IEquipmentLibrary
{
    IReadOnlyList<SpectrographProfile> LoadSpectrographs();
    void SaveSpectrographs(IReadOnlyList<SpectrographProfile> profiles);

    IReadOnlyList<TelescopeProfile> LoadTelescopes();
    void SaveTelescopes(IReadOnlyList<TelescopeProfile> profiles);

    IReadOnlyList<CameraProfile> LoadCameras();
    void SaveCameras(IReadOnlyList<CameraProfile> profiles);

    IReadOnlyList<EquipmentSetup> LoadSetups();
    void SaveSetups(IReadOnlyList<EquipmentSetup> setups);
}
