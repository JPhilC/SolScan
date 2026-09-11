using CommunityToolkit.Mvvm.ComponentModel;
using SolScan.Core.Equipment;

namespace SolScan.App.ViewModels.Equipment;

/// <summary>
/// Mutable, bindable wrapper around an (immutable) TelescopeProfile record - same role as
/// SpectrographProfileEditor/CameraProfileEditor.
/// </summary>
public partial class TelescopeProfileEditor : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty]
    private string label;

    [ObservableProperty]
    private double? focalLengthMm;

    [ObservableProperty]
    private double? apertureMm;

    [ObservableProperty]
    private string? energyRejectionFilter;

    public TelescopeProfileEditor(TelescopeProfile profile)
    {
        Id = profile.Id;
        label = profile.Label;
        focalLengthMm = profile.FocalLengthMm;
        apertureMm = profile.ApertureMm;
        energyRejectionFilter = profile.EnergyRejectionFilter;
    }

    public TelescopeProfile ToModel() => new(Id, Label, FocalLengthMm, ApertureMm, EnergyRejectionFilter);
}
