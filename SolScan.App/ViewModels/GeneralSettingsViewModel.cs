using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SolScan.Core.Capture;
using SolScan.Core.Telescope;

namespace SolScan.App.ViewModels;

/// <summary>
/// Options > General tab: app-wide settings not tied to a specific camera model or equipment
/// profile - where new recordings are saved, how to reach the ASCOM Alpaca mount, and the site
/// location Prepare's mount connection reconciles against. Backed by <see cref="IAppSettingsStore"/>,
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

    /// <summary>Defaults to <see cref="AlpacaDefaults.DefaultBaseUrl"/> when nothing's been
    /// customized - same null-means-default treatment as <see cref="CapturesRootFolder"/>.</summary>
    [ObservableProperty]
    private string alpacaBaseUrl;

    [ObservableProperty]
    private int alpacaDeviceNumber;

    [ObservableProperty]
    private double siteLatitudeDeg;

    [ObservableProperty]
    private double siteLongitudeDeg;

    [ObservableProperty]
    private double siteElevationM;

    public GeneralSettingsViewModel(IAppSettingsStore store)
    {
        _store = store;
        var settings = store.Load();
        capturesRootFolder = settings.CapturesRootFolder ?? CaptureLocations.DefaultCapturesRootFolder;
        alpacaBaseUrl = settings.AlpacaBaseUrl ?? AlpacaDefaults.DefaultBaseUrl;
        alpacaDeviceNumber = settings.AlpacaDeviceNumber;
        siteLatitudeDeg = settings.SiteLatitudeDeg;
        siteLongitudeDeg = settings.SiteLongitudeDeg;
        siteElevationM = settings.SiteElevationM;
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

    [RelayCommand]
    private void ResetAlpacaBaseUrlToDefault() => AlpacaBaseUrl = AlpacaDefaults.DefaultBaseUrl;

    /// <summary>Called from <see cref="OptionsViewModel.Save"/>, not its own [RelayCommand] - the
    /// Options view has one shared "Save" button for all its tabs.</summary>
    public void Save()
    {
        // Persisted as null (not the literal default path/URL) whenever it still equals SolScan's
        // own current default, so a future change to that default is picked up automatically for
        // anyone who's never actually customized this, rather than being locked in by whatever the
        // default happened to be when this was last saved.
        var rootFolderToSave = string.Equals(CapturesRootFolder, CaptureLocations.DefaultCapturesRootFolder, StringComparison.OrdinalIgnoreCase)
            ? null
            : CapturesRootFolder;
        var alpacaBaseUrlToSave = string.Equals(AlpacaBaseUrl, AlpacaDefaults.DefaultBaseUrl, StringComparison.OrdinalIgnoreCase)
            ? null
            : AlpacaBaseUrl;

        _store.Save(new AppSettings(
            rootFolderToSave,
            alpacaBaseUrlToSave,
            AlpacaDeviceNumber,
            SiteLatitudeDeg,
            SiteLongitudeDeg,
            SiteElevationM));
    }
}
