using CommunityToolkit.Mvvm.ComponentModel;
using SolScan.Core.Equipment;

namespace SolScan.App.ViewModels.Equipment;

/// <summary>
/// Mutable, bindable wrapper around an (immutable) CameraProfile record - same role as
/// SpectrographProfileEditor/TelescopeProfileEditor. Most entries here are auto-added by
/// CaptureViewModel when a new camera model first connects (see CameraProfile's doc comment), but
/// the Options > Cameras tab this backs still allows adding/editing one by hand too.
/// </summary>
public partial class CameraProfileEditor : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty]
    private string label;

    [ObservableProperty]
    private double? pixelSizeMicrons;

    public CameraProfileEditor(CameraProfile profile)
    {
        Id = profile.Id;
        label = profile.Label;
        pixelSizeMicrons = profile.PixelSizeMicrons;
    }

    public CameraProfile ToModel() => new(Id, Label, PixelSizeMicrons);
}
