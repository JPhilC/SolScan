using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolScan.App.ViewModels.Equipment;
using SolScan.App.ViewModels.Processing;
using SolScan.Core.Capture;
using SolScan.Core.Equipment;
using SolScan.Core.Processing;

namespace SolScan.App.ViewModels;

/// <summary>
/// SolScan's equivalent of JSolex's "Equipment" menu (SpectroHeliographEditor.java +
/// SetupEditor.java), embedded directly in the Options view rather than opened as separate modal
/// dialogs: a library of spectrographs, a library of telescopes, a library of cameras (mostly
/// populated automatically - see CameraProfile's doc comment), and - new to SolScan, see
/// EquipmentSetup - a library of saved SHG+telescope combinations that Prepare picks from. Also
/// carries a General tab (<see cref="General"/>) for app-wide settings that aren't equipment at
/// all - currently just where recordings are saved - and three Processing tabs
/// (<see cref="ProcessParameters"/>/<see cref="ImageEnhancement"/>/<see cref="ImageSelection"/>),
/// SolScan's equivalent of JSolex's own "Process parameters" dialog - shown here, per-run in JSolex,
/// rather than as a separate modal dialog, since SolScan persists one global default the same way
/// JSolex's own dialog does on every OK anyway.
/// </summary>
public partial class OptionsViewModel : ObservableObject
{
    private readonly IProcessParamsStore _processParamsStore;

    public GeneralSettingsViewModel General { get; }
    public SpectrographLibraryViewModel Spectrographs { get; }
    public TelescopeLibraryViewModel Telescopes { get; }
    public CameraLibraryViewModel Cameras { get; }
    public EquipmentSetupLibraryViewModel Setups { get; }
    public ProcessParametersViewModel ProcessParameters { get; }
    public ImageEnhancementViewModel ImageEnhancement { get; }
    public ImageSelectionViewModel ImageSelection { get; }

    [ObservableProperty]
    private string statusText = string.Empty;

    public OptionsViewModel(IEquipmentLibrary library, IAppSettingsStore appSettingsStore, IProcessParamsStore processParamsStore)
    {
        _processParamsStore = processParamsStore;

        General = new GeneralSettingsViewModel(appSettingsStore);
        Spectrographs = new SpectrographLibraryViewModel(library);
        Telescopes = new TelescopeLibraryViewModel(library);
        Cameras = new CameraLibraryViewModel(library);
        Setups = new EquipmentSetupLibraryViewModel(library, Spectrographs.Items, Telescopes.Items);

        // ProcessParameters/ImageEnhancement/ImageSelection all edit different slices of the *same*
        // persisted ProcessParams record, so (unlike the equipment tabs above, each of which owns its
        // own independent slice of IEquipmentLibrary) they're seeded from one Load() here and
        // reassembled into one Save() call below, rather than each saving through the store
        // independently and risking clobbering each other's edits.
        var processParams = processParamsStore.Load();
        ProcessParameters = new ProcessParametersViewModel(processParams);
        ImageEnhancement = new ImageEnhancementViewModel(processParams);
        ImageSelection = new ImageSelectionViewModel(processParams);
    }

    [RelayCommand]
    private void Save()
    {
        General.Save();
        Spectrographs.Save();
        Telescopes.Save();
        Cameras.Save();
        Setups.Save();

        _processParamsStore.Save(new ProcessParams(
            ImageSelection.ToRequestedImages(),
            ProcessParameters.ToSpectrumParams(),
            ProcessParameters.ToGeometryParams(),
            ImageEnhancement.ToContrastEnhancement()));

        StatusText = "Saved.";
    }
}
