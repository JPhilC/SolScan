using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolScan.App.Services;
using SolScan.Core.Capture;
using SolScan.Core.Equipment;
using SolScan.Core.Telescope;
using SolScan.Infrastructure.Telescope;

namespace SolScan.App.ViewModels;

/// <summary>
/// Prepare stage: connect/disconnect the mount, Park/Unpark, a manual tracking toggle, live RA/Dec
/// + status, and the site (latitude/longitude/elevation) SolScan reconciles against a connecting
/// mount - ported from RASTA's SettingsViewModel/PrepareViewModel mount-and-site machinery (see
/// SolScan CLAUDE.md Phase 2), trimmed to what SolScan.Core.Telescope.ITelescopeMount actually
/// exposes (equatorial-only, no Az/Alt/Home/tracking-rate). Registered as a DI singleton (not
/// transient, unlike most other stage view models) so connection state and command CanExecute
/// survive navigating away to Capture/Process/Options and back - mirrors RASTA's own
/// PrepareViewModel/SettingsViewModel, which are AddScoped in a single-composition-root app (i.e.
/// effectively singletons there too).
/// </summary>
public partial class PrepareViewModel : ObservableObject
{
    // Loose enough to absorb float/round-trip noise, tight enough that a genuinely different site
    // (or a mistyped value) still trips it - same tolerances RASTA's SettingsViewModel uses.
    private const double LatLonToleranceDeg = 0.01; // ~1 km at the equator
    private const double ElevationToleranceM = 5.0;

    private static readonly TimeSpan ParkTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ParkPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly ITelescopeMount _mount;
    private readonly MountState _mountState;
    private readonly MountService _mountService;
    private readonly IAppSettingsStore _appSettingsStore;
    private readonly IEquipmentLibrary _equipmentLibrary;

    // Guards against feedback loops: true while a property is being set from a freshly-loaded
    // settings file or from MountState (the mount's own reported value), rather than by the user -
    // so the corresponding On...Changed handler doesn't re-save/re-push what was just loaded.
    private bool _isSyncing;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private bool isTracking;

    [ObservableProperty]
    private bool isSlewing;

    [ObservableProperty]
    private bool isParked;

    [ObservableProperty]
    private double rightAscensionHours;

    [ObservableProperty]
    private double declinationDeg;

    [ObservableProperty]
    private double siteLatitudeDeg;

    [ObservableProperty]
    private double siteLongitudeDeg;

    [ObservableProperty]
    private double siteElevationM;

    [ObservableProperty]
    private string statusText = "Not connected.";

    // -----------------------------
    // Equipment - which SHG+telescope combo (an EquipmentSetup, from Options > Setups) is in use
    // this session. The camera isn't picked here at all - see CaptureViewModel's camera auto-add.
    // -----------------------------

    public ObservableCollection<EquipmentSetup> AvailableEquipmentSetups { get; } = [];

    [ObservableProperty]
    private EquipmentSetup? selectedEquipmentSetup;

    /// <summary>What <see cref="SelectedEquipmentSetup"/> actually resolves to, for display -
    /// "(none)" rather than blank so it reads as "nothing picked" rather than "still loading".</summary>
    [ObservableProperty]
    private string selectedSpectrographLabel = "(none)";

    [ObservableProperty]
    private string selectedTelescopeLabel = "(none)";

    public PrepareViewModel(
        ITelescopeMount mount,
        MountState mountState,
        MountService mountService,
        IAppSettingsStore appSettingsStore,
        IEquipmentLibrary equipmentLibrary)
    {
        _mount = mount;
        _mountState = mountState;
        _mountService = mountService;
        _appSettingsStore = appSettingsStore;
        _equipmentLibrary = equipmentLibrary;

        _isSyncing = true;
        var settings = _appSettingsStore.Load();
        SiteLatitudeDeg = settings.SiteLatitudeDeg;
        SiteLongitudeDeg = settings.SiteLongitudeDeg;
        SiteElevationM = settings.SiteElevationM;

        IsConnected = _mountState.IsConnected;
        IsTracking = _mountState.IsTracking;
        IsSlewing = _mountState.IsSlewing;
        IsParked = _mountState.IsParked;
        RightAscensionHours = _mountState.RightAscensionHours;
        DeclinationDeg = _mountState.DeclinationDeg;

        LoadEquipmentSetups();
        SelectedEquipmentSetup = AvailableEquipmentSetups.FirstOrDefault(s => s.Id == settings.SelectedEquipmentSetupId);
        _isSyncing = false;

        _mountState.PropertyChanged += MountStatePropertyChanged;
    }

    /// <summary>(Re)loads <see cref="AvailableEquipmentSetups"/> from the library - also exposed as
    /// a command so the picker can be refreshed after adding/editing a Setup in Options without
    /// requiring an app restart (PrepareViewModel is a long-lived singleton, so it isn't naturally
    /// re-constructed on navigating back here - see the class doc comment).</summary>
    private void LoadEquipmentSetups()
    {
        var previouslySelectedId = SelectedEquipmentSetup?.Id;
        AvailableEquipmentSetups.Clear();
        foreach (var setup in _equipmentLibrary.LoadSetups())
        {
            AvailableEquipmentSetups.Add(setup);
        }

        SelectedEquipmentSetup = AvailableEquipmentSetups.FirstOrDefault(s => s.Id == previouslySelectedId);
    }

    [RelayCommand]
    private void RefreshEquipment() => LoadEquipmentSetups();

    partial void OnSelectedEquipmentSetupChanged(EquipmentSetup? value)
    {
        var spectrograph = value is null ? null : _equipmentLibrary.LoadSpectrographs().FirstOrDefault(s => s.Id == value.SpectrographProfileId);
        var telescope = value is null ? null : _equipmentLibrary.LoadTelescopes().FirstOrDefault(t => t.Id == value.TelescopeProfileId);
        SelectedSpectrographLabel = spectrograph?.Label ?? "(none)";
        SelectedTelescopeLabel = telescope?.Label ?? "(none)";

        if (_isSyncing)
            return;

        var current = _appSettingsStore.Load();
        _appSettingsStore.Save(current with { SelectedEquipmentSetupId = value?.Id });
    }

    // -----------------------------
    // Reacting to MountState - the live, poll-refreshed status MountService writes to (also what
    // App.xaml.cs's mount-connection-lost handler resets IsConnected on).
    // -----------------------------

    private void MountStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MountState.IsConnected):
                IsConnected = _mountState.IsConnected;
                break;
            case nameof(MountState.RightAscensionHours):
                RightAscensionHours = _mountState.RightAscensionHours;
                break;
            case nameof(MountState.DeclinationDeg):
                DeclinationDeg = _mountState.DeclinationDeg;
                break;
            case nameof(MountState.IsTracking):
                _isSyncing = true;
                IsTracking = _mountState.IsTracking;
                _isSyncing = false;
                break;
            case nameof(MountState.IsSlewing):
                IsSlewing = _mountState.IsSlewing;
                break;
            case nameof(MountState.IsParked):
                IsParked = _mountState.IsParked;
                break;
        }
    }

    partial void OnIsConnectedChanged(bool value)
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ParkCommand.NotifyCanExecuteChanged();
        UnparkCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ParkCommand.NotifyCanExecuteChanged();
        UnparkCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsParkedChanged(bool value)
    {
        ParkCommand.NotifyCanExecuteChanged();
        UnparkCommand.NotifyCanExecuteChanged();
    }

    // -----------------------------
    // Connect / disconnect
    // -----------------------------

    public bool CanConnect => !IsConnected && !IsBusy;
    public bool CanDisconnect => IsConnected && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        try
        {
            IsBusy = true;
            StatusText = "Connecting…";

            // Read fresh - same "Options takes effect on the very next use, no restart" pattern
            // CaptureViewModel follows for CapturesRootFolder.
            var settings = _appSettingsStore.Load();
            if (_mount is AscomTelescopeMount ascomMount)
            {
                ascomMount.SetBaseUrl(settings.AlpacaBaseUrl ?? AlpacaDefaults.DefaultBaseUrl);
                ascomMount.SetDeviceNumber(settings.AlpacaDeviceNumber);
            }

            await _mount.ConnectAsync();
            IsConnected = _mountState.IsConnected = _mount.IsConnected;

            if (!_mount.IsConnected)
            {
                StatusText = "Failed to connect.";
                return;
            }

            // ---------------------------------------------------------
            // Check parked state
            // ---------------------------------------------------------
            var wasParked = await _mount.GetAtParkAsync();
            _mountState.WasParkedOnConnect = wasParked;
            if (wasParked)
            {
                var unparkResult = MessageBox.Show(
                    "The telescope is currently parked. Do you want to unpark it?",
                    "Telescope Parked",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (unparkResult == MessageBoxResult.Yes)
                {
                    await _mount.UnparkAsync();
                }
                else
                {
                    // Graceful degradation: telescope stays parked.
                    IsParked = _mountState.IsParked = true;
                    StatusText = "Connected (parked).";
                    _mountService.Start();
                    return;
                }
            }

            // ---------------------------------------------------------
            // Reconcile site values: SolScan's site settings can be entered and used before any
            // mount is ever connected, so a connecting mount's own site settings can't just be
            // pulled in unconditionally - that would silently overwrite a deliberately-entered
            // SolScan value with whatever the mount happens to report, and always pushing SolScan's
            // value to the mount would just as wrongly stomp a mount genuinely set up correctly for
            // a different location. Compare the two and ask only when they actually disagree.
            // ---------------------------------------------------------
            var mountLat = _mount.SiteLatitudeDeg;
            var mountLon = _mount.SiteLongitudeDeg;
            var mountElevM = _mount.SiteElevationM;

            var sitesDiffer =
                Math.Abs(mountLat - SiteLatitudeDeg) > LatLonToleranceDeg ||
                Math.Abs(mountLon - SiteLongitudeDeg) > LatLonToleranceDeg ||
                Math.Abs(mountElevM - SiteElevationM) > ElevationToleranceM;

            var pullFromMount = true;
            if (sitesDiffer)
            {
                var message =
                    "The connected mount's site settings differ from what's currently set in SolScan:\n\n" +
                    "              SolScan        Mount\n" +
                    $"Latitude:   {SiteLatitudeDeg,9:F5}°   {mountLat,9:F5}°\n" +
                    $"Longitude:  {SiteLongitudeDeg,9:F5}°   {mountLon,9:F5}°\n" +
                    $"Elevation:  {SiteElevationM,9:F1} m   {mountElevM,9:F1} m\n\n" +
                    "Update the MOUNT to match SolScan (Yes), or update SolScan to match the MOUNT (No)?";

                var siteResult = MessageBox.Show(
                    message,
                    "Site Settings Differ",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                pullFromMount = siteResult == MessageBoxResult.No;
            }

            _isSyncing = true;
            if (pullFromMount)
            {
                SiteLatitudeDeg = mountLat;
                SiteLongitudeDeg = mountLon;
                SiteElevationM = mountElevM;
                PersistSiteSettings();
            }
            else
            {
                await _mount.SetSiteLatitudeAsync(SiteLatitudeDeg);
                await _mount.SetSiteLongitudeAsync(SiteLongitudeDeg);
                await _mount.SetSiteElevationAsync(SiteElevationM);
            }
            _isSyncing = false;

            _mountState.SiteLatitudeDeg = SiteLatitudeDeg;
            _mountState.SiteLongitudeDeg = SiteLongitudeDeg;
            _mountState.SiteElevationM = SiteElevationM;

            // ---------------------------------------------------------
            // Tracking - reflect whatever the mount is actually doing rather than forcing it on;
            // the checkbox is how the user turns tracking on/off explicitly.
            // ---------------------------------------------------------
            _isSyncing = true;
            IsTracking = _mountState.IsTracking = await _mount.GetTrackingAsync();
            _isSyncing = false;

            StatusText = "Connected.";
            _mountService.Start();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        try
        {
            IsBusy = true;

            if (_mountState.WasParkedOnConnect)
            {
                var parkResult = MessageBox.Show(
                    "Do you want to park the telescope before disconnecting?",
                    "Park Telescope",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (parkResult == MessageBoxResult.Yes)
                {
                    StatusText = "Parking…";
                    await ParkAndWaitAsync();
                }
            }

            _mountService.Stop();
            await _mount.DisconnectAsync();
            IsConnected = _mountState.IsConnected = _mount.IsConnected;
            StatusText = "Disconnected.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // -----------------------------
    // Park / unpark
    // -----------------------------

    public bool CanPark => IsConnected && !IsParked && !IsBusy;
    public bool CanUnpark => IsConnected && IsParked && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanPark))]
    private async Task ParkAsync()
    {
        try
        {
            IsBusy = true;
            StatusText = "Parking…";
            await ParkAndWaitAsync();
            StatusText = IsParked ? "Parked." : "Parking timed out.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUnpark))]
    private async Task UnparkAsync()
    {
        try
        {
            IsBusy = true;
            StatusText = "Unparking…";
            await _mount.UnparkAsync();
            IsParked = _mountState.IsParked = false;
            StatusText = "Unparked.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Issues Park then polls "at park" until it reports true, bounded by
    /// <see cref="ParkTimeout"/> - same shape RASTA's SettingsViewModel used to wait out a slew.</summary>
    private async Task ParkAndWaitAsync()
    {
        await _mount.ParkAsync();

        var start = DateTime.UtcNow;
        var parked = false;
        while (DateTime.UtcNow - start < ParkTimeout)
        {
            parked = await _mount.GetAtParkAsync();
            if (parked)
                break;

            await Task.Delay(ParkPollInterval);
        }

        IsParked = _mountState.IsParked = parked;
    }

    // -----------------------------
    // Tracking toggle
    // -----------------------------

    partial void OnIsTrackingChanged(bool value)
    {
        if (_isSyncing || !IsConnected)
            return;

        _mountState.IsTracking = value;
        _ = _mount.SetTrackingAsync(value);
    }

    // -----------------------------
    // Site settings - editable at any time, with or without a mount attached; persisted
    // immediately and pushed to a connected mount.
    // -----------------------------

    private void PersistSiteSettings()
    {
        var current = _appSettingsStore.Load();
        _appSettingsStore.Save(current with
        {
            SiteLatitudeDeg = SiteLatitudeDeg,
            SiteLongitudeDeg = SiteLongitudeDeg,
            SiteElevationM = SiteElevationM,
        });
    }

    partial void OnSiteLatitudeDegChanged(double value)
    {
        if (_isSyncing)
            return;

        PersistSiteSettings();
        if (IsConnected)
            _ = _mount.SetSiteLatitudeAsync(value);
    }

    partial void OnSiteLongitudeDegChanged(double value)
    {
        if (_isSyncing)
            return;

        PersistSiteSettings();
        if (IsConnected)
            _ = _mount.SetSiteLongitudeAsync(value);
    }

    partial void OnSiteElevationMChanged(double value)
    {
        if (_isSyncing)
            return;

        PersistSiteSettings();
        if (IsConnected)
            _ = _mount.SetSiteElevationAsync(value);
    }
}
