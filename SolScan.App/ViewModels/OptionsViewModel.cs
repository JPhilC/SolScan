using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolScan.App.ViewModels.Equipment;
using SolScan.Core.Capture;
using SolScan.Core.Equipment;

namespace SolScan.App.ViewModels;

/// <summary>
/// SolScan's equivalent of JSolex's "Equipment" menu (SpectroHeliographEditor.java +
/// SetupEditor.java), embedded directly in the Options view rather than opened as separate modal
/// dialogs: a library of spectrographs, a library of telescopes, a library of cameras (mostly
/// populated automatically - see CameraProfile's doc comment), and - new to SolScan, see
/// EquipmentSetup - a library of saved SHG+telescope combinations that Prepare picks from. Also
/// carries a General tab (<see cref="General"/>) for app-wide settings that aren't equipment at
/// all - currently just where recordings are saved.
/// </summary>
public partial class OptionsViewModel : ObservableObject
{
    public GeneralSettingsViewModel General { get; }
    public SpectrographLibraryViewModel Spectrographs { get; }
    public TelescopeLibraryViewModel Telescopes { get; }
    public CameraLibraryViewModel Cameras { get; }
    public EquipmentSetupLibraryViewModel Setups { get; }

    [ObservableProperty]
    private string statusText = string.Empty;

    public OptionsViewModel(IEquipmentLibrary library, IAppSettingsStore appSettingsStore)
    {
        General = new GeneralSettingsViewModel(appSettingsStore);
        Spectrographs = new SpectrographLibraryViewModel(library);
        Telescopes = new TelescopeLibraryViewModel(library);
        Cameras = new CameraLibraryViewModel(library);
        Setups = new EquipmentSetupLibraryViewModel(library, Spectrographs.Items, Telescopes.Items);
    }

    [RelayCommand]
    private void Save()
    {
        General.Save();
        Spectrographs.Save();
        Telescopes.Save();
        Cameras.Save();
        Setups.Save();
        StatusText = "Saved.";
    }
}
