using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolScan.App.ViewModels.Equipment;
using SolScan.Core.Equipment;

namespace SolScan.App.ViewModels;

/// <summary>
/// SolScan's equivalent of JSolex's "Equipment" menu (SpectroHeliographEditor.java +
/// SetupEditor.java), embedded directly in the Options view rather than opened as separate modal
/// dialogs: a library of spectrographs, a library of telescope/camera equipment profiles, and -
/// new to SolScan, see EquipmentSetup - a library of saved SHG+equipment combinations that
/// Prepare/Capture will pick from once those stages exist (Phase 2+ of SolScan CLAUDE.md).
/// </summary>
public partial class OptionsViewModel : ObservableObject
{
    public SpectrographLibraryViewModel Spectrographs { get; }
    public EquipmentProfileLibraryViewModel EquipmentProfiles { get; }
    public EquipmentSetupLibraryViewModel Setups { get; }

    [ObservableProperty]
    private string statusText = string.Empty;

    public OptionsViewModel(IEquipmentLibrary library)
    {
        Spectrographs = new SpectrographLibraryViewModel(library);
        EquipmentProfiles = new EquipmentProfileLibraryViewModel(library);
        Setups = new EquipmentSetupLibraryViewModel(library, Spectrographs.Items, EquipmentProfiles.Items);
    }

    [RelayCommand]
    private void Save()
    {
        Spectrographs.Save();
        EquipmentProfiles.Save();
        Setups.Save();
        StatusText = "Saved.";
    }
}
