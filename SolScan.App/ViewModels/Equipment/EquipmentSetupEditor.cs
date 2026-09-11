using CommunityToolkit.Mvvm.ComponentModel;
using SolScan.Core.Equipment;

namespace SolScan.App.ViewModels.Equipment;

/// <summary>
/// Mutable, bindable wrapper around an (immutable) EquipmentSetup record - a saved SHG+telescope
/// combination. Unlike SpectrographProfileEditor/TelescopeProfileEditor, this one holds references
/// (via ComboBox SelectedItem bindings) into the *other* two editors' live collections rather than
/// copying their fields, so the setup always reflects whatever those profiles currently say.
/// </summary>
public partial class EquipmentSetupEditor : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty]
    private string label;

    [ObservableProperty]
    private SpectrographProfileEditor? spectrograph;

    [ObservableProperty]
    private TelescopeProfileEditor? telescope;

    public EquipmentSetupEditor(
        EquipmentSetup setup,
        IEnumerable<SpectrographProfileEditor> availableSpectrographs,
        IEnumerable<TelescopeProfileEditor> availableTelescopes)
    {
        Id = setup.Id;
        label = setup.Label;
        spectrograph = availableSpectrographs.FirstOrDefault(s => s.Id == setup.SpectrographProfileId);
        telescope = availableTelescopes.FirstOrDefault(t => t.Id == setup.TelescopeProfileId);
    }

    public EquipmentSetupEditor(string label, SpectrographProfileEditor? spectrograph, TelescopeProfileEditor? telescope)
    {
        Id = Guid.NewGuid();
        this.label = label;
        this.spectrograph = spectrograph;
        this.telescope = telescope;
    }

    /// <summary>Null when either side hasn't been picked yet - callers should skip such setups on save.</summary>
    public EquipmentSetup? ToModel() =>
        Spectrograph is null || Telescope is null
            ? null
            : new EquipmentSetup(Id, Label, Spectrograph.Id, Telescope.Id);
}
