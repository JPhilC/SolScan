using CommunityToolkit.Mvvm.ComponentModel;
using SolScan.Core.Equipment;

namespace SolScan.App.ViewModels.Equipment;

/// <summary>
/// Mutable, bindable wrapper around an (immutable) EquipmentProfile record - same role as
/// SpectrographProfileEditor, but for the telescope/camera/mount side. Mirrors astro4j's
/// SetupEditor.java.
/// </summary>
public partial class EquipmentProfileEditor : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty]
    private string label;

    [ObservableProperty]
    private string? telescope;

    [ObservableProperty]
    private double? telescopeFocalLengthMm;

    [ObservableProperty]
    private double? apertureMm;

    [ObservableProperty]
    private string? camera;

    [ObservableProperty]
    private double? cameraPixelSizeMicrons;

    [ObservableProperty]
    private string? mount;

    [ObservableProperty]
    private double? siteLatitudeDeg;

    [ObservableProperty]
    private double? siteLongitudeDeg;

    public EquipmentProfileEditor(EquipmentProfile profile)
    {
        Id = profile.Id;
        label = profile.Label;
        telescope = profile.Telescope;
        telescopeFocalLengthMm = profile.TelescopeFocalLengthMm;
        apertureMm = profile.ApertureMm;
        camera = profile.Camera;
        cameraPixelSizeMicrons = profile.CameraPixelSizeMicrons;
        mount = profile.Mount;
        siteLatitudeDeg = profile.SiteLatitudeDeg;
        siteLongitudeDeg = profile.SiteLongitudeDeg;
    }

    public EquipmentProfile ToModel() => new(
        Id,
        Label,
        Telescope,
        TelescopeFocalLengthMm,
        ApertureMm,
        Camera,
        CameraPixelSizeMicrons,
        Mount,
        SiteLatitudeDeg,
        SiteLongitudeDeg);
}
