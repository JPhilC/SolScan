using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SolScan.Core.Capture;

namespace SolScan.App.ViewModels;

/// <summary>
/// Options > General tab: app-wide settings not tied to a specific camera model or equipment
/// profile - currently just where new recordings are saved. Backed by <see cref="IAppSettingsStore"/>,
/// same "own its own Save(), called from OptionsViewModel.Save" shape as the equipment library
/// tabs' view models.
/// </summary>
public partial class GeneralSettingsViewModel : ObservableObject
{
    private readonly IAppSettingsStore _store;

    /// <summary>Defaults to <see cref="CaptureLocations.DefaultCapturesRootFolder"/> when nothing's
    /// been customized, so the textbox always shows a real, usable path rather than a bare "not
    /// set".</summary>
    [ObservableProperty]
    private string capturesRootFolder;

    public GeneralSettingsViewModel(IAppSettingsStore store)
    {
        _store = store;
        capturesRootFolder = store.Load().CapturesRootFolder ?? CaptureLocations.DefaultCapturesRootFolder;
    }

    [RelayCommand]
    private void BrowseForCapturesRootFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where SolScan saves recordings",
            InitialDirectory = Directory.Exists(CapturesRootFolder) ? CapturesRootFolder : CaptureLocations.DefaultCapturesRootFolder,
        };

        if (dialog.ShowDialog() == true && dialog.FolderName is { Length: > 0 } chosen)
        {
            CapturesRootFolder = chosen;
        }
    }

    [RelayCommand]
    private void ResetCapturesRootFolderToDefault() => CapturesRootFolder = CaptureLocations.DefaultCapturesRootFolder;

    /// <summary>Called from <see cref="OptionsViewModel.Save"/>, not its own [RelayCommand] - the
    /// Options view has one shared "Save" button for all its tabs.</summary>
    public void Save()
    {
        // Persisted as null (not the literal default path) whenever it still equals SolScan's own
        // current default, so a future change to that default is picked up automatically for
        // anyone who's never actually customized this, rather than being locked in by whatever the
        // default happened to be when this was last saved.
        var toSave = string.Equals(CapturesRootFolder, CaptureLocations.DefaultCapturesRootFolder, StringComparison.OrdinalIgnoreCase)
            ? null
            : CapturesRootFolder;
        _store.Save(new AppSettings(toSave));
    }
}
