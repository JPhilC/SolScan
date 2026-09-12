using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SolScan.App.Services;
using SolScan.App.ViewModels;
using SolScan.App.Views;
using SolScan.Core.Camera;
using SolScan.Core.Capture;
using SolScan.Core.Equipment;
using SolScan.Core.Processing;
using SolScan.Core.Telescope;
using SolScan.Infrastructure.Camera;
using SolScan.Infrastructure.Camera.Altair;
using SolScan.Infrastructure.Camera.Asi;
using SolScan.Infrastructure.Capture;
using SolScan.Infrastructure.Equipment;
using SolScan.Infrastructure.Processing;
using SolScan.Infrastructure.Telescope;
using SolScan.Processing.Shg;
using SolScan.Simulators;

namespace SolScan.App;

/// <summary>
/// Interaction logic for App.xaml.
///
/// Single composition root: one ServiceCollection is built once here at startup - no scopes
/// created afterwards, so AddScoped registrations behave like singletons for the app's
/// lifetime. Mirrors RASTA.App/App.xaml.cs deliberately.
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan SplashMinimumDuration = TimeSpan.FromSeconds(2);

    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Shown here (rather than via the SplashScreen build action - see the note in
        // SolScan.App.csproj) so SolScanSplash.png's transparent background actually renders
        // transparent. No StartupUri in App.xaml, so nothing else creates a window until
        // MainWindow is explicitly shown further down. Mirrors RASTA.App/App.xaml.cs.
        var splash = new SplashWindow();
        splash.Show();
        var splashShownAt = DateTime.UtcNow;

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // See OnMountConnectionLost below - fires on MountService's own polling background thread
        // whenever a live mount call throws (the only signal available that the link has actually
        // gone away).
        _serviceProvider.GetRequiredService<MountService>().ConnectionLost += OnMountConnectionLost;

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.ContentRendered += async (_, _) =>
        {
            // DI setup above is fast enough that the splash would otherwise vanish almost
            // instantly - hold it up to a minimum duration so it's actually visible, without
            // blocking the UI thread (Task.Delay + await, not Thread.Sleep).
            var remaining = SplashMinimumDuration - (DateTime.UtcNow - splashShownAt);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining);
            }
            splash.Close();
        };
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // ASCOM Alpaca (REST) mount - see SolScan CLAUDE.md "Scope for v1" for why Alpaca-only, not
        // direct COM. AscomTelescopeMount's base URL/device number are set from Options > General
        // at connect time (PrepareViewModel), not here - see AscomTelescopeMount.SetBaseUrl.
        services.AddSingleton<AscomAlpacaClient>();
        services.AddSingleton<ITelescopeMount>(sp => new AscomTelescopeMount(sp.GetRequiredService<AscomAlpacaClient>()));
        services.AddSingleton<MountState>();
        services.AddSingleton<MountService>();

        services.AddSingleton<IEquipmentLibrary, JsonEquipmentLibrary>();

        // App-wide settings not tied to a specific camera model - currently just where new
        // recordings are saved (Options > General).
        services.AddSingleton<IAppSettingsStore, JsonAppSettingsStore>();

        // One ICameraProvider per vendor (plus the hardware-free simulator) - aggregated by
        // ICameraDiscoveryService for the Capture view's camera picker. Each provider degrades to
        // an empty Discover() if its native SDK DLL isn't present - see
        // SolScan.External/x64/ASI/README.md / SolScan.External/x64/Altair/README.md.
        services.AddSingleton<ICameraProvider, AsiCameraProvider>();
        services.AddSingleton<ICameraProvider, AltairCameraProvider>();
        services.AddSingleton<ICameraProvider, SimulatedCameraProvider>();
        services.AddSingleton<ICameraDiscoveryService, CameraDiscoveryService>();

        // Per-camera-model (keyed by name, e.g. "ZWO ASI678MM") dial-in settings, remembered
        // across sessions - see ICameraSettingsStore's own doc comment for why Name, not Id.
        services.AddSingleton<ICameraSettingsStore, JsonCameraSettingsStore>();

        // Transient so CaptureViewModel gets a fresh writer per recording; resolved via a factory
        // delegate rather than an injected IServiceProvider, to keep the view model out of the
        // service-locator pattern.
        services.AddTransient<ISerWriter, SerWriter>();
        services.AddSingleton<Func<ISerWriter>>(sp => sp.GetRequiredService<ISerWriter>);

        // Read-side counterpart, used by ProcessViewModel to open a finished capture rather than a
        // live camera stream - same factory-delegate shape as ISerWriter above, for the same
        // "fresh instance per file, view model stays out of the service-locator pattern" reasoning.
        services.AddTransient<ISerReader, SerReader>();
        services.AddSingleton<Func<ISerReader>>(sp => sp.GetRequiredService<ISerReader>);

        // One global set of processing defaults (which images to generate, spectral line/geometry
        // setup, contrast method) - edited across three Options tabs, see OptionsViewModel.
        services.AddSingleton<IProcessParamsStore, JsonProcessParamsStore>();

        // Real spectral-line-curvature detection + reconstruction (Raw/Reconstruction/Continuum) -
        // see ShgProcessor's own doc comment for what it does and doesn't cover yet. Transient: no
        // state to share across uses, each Process click gets a fresh instance.
        services.AddTransient<IShgProcessor, ShgProcessor>();

        // Snapshots the SHG/telescope/camera used (plus camera dial-in settings/mount pointing at
        // recording start - see CaptureMetadata) into a .equipment.json sidecar per recording - see
        // CaptureViewModel.WriteCaptureMetadata. Stateless, so a singleton is fine.
        services.AddSingleton<ICaptureMetadataStore, JsonCaptureMetadataStore>();

        // The pop-out, modeless Hand Control window (see HandControlWindow.xaml) - transient, one
        // fresh instance per open, resolved via a factory delegate rather than an injected
        // IServiceProvider, same "keep the view model out of the service-locator pattern" reasoning
        // as ISerWriter's factory above. CaptureViewModel tracks whether one's already open itself.
        services.AddTransient<HandControlViewModel>();
        services.AddTransient<HandControlWindow>();
        services.AddSingleton<Func<HandControlWindow>>(sp => sp.GetRequiredService<HandControlWindow>);

        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<NavigationViewModel>();

        // Singleton, not transient like the other stage view models - mount connection state
        // (IsBusy, command CanExecute, the MountState.PropertyChanged subscription) must survive
        // navigating away to Capture/Process/Options and back.
        services.AddSingleton<PrepareViewModel>();
        services.AddTransient<CaptureViewModel>();
        services.AddTransient<ProcessViewModel>();
        services.AddTransient<OptionsViewModel>();
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stop MountService's poll loop before the service provider (and everything it owns) goes
        // away - without this it keeps running past window close and can still try to marshal
        // updates onto Application.Current.Dispatcher after Application.Current has already gone
        // null. Best-effort: a shutdown-time failure here shouldn't block exit.
        try
        {
            _serviceProvider?.GetService<MountService>()?.Stop();
        }
        catch
        {
            // Ignored - the app is exiting regardless.
        }

        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Fires when MountService's poll loop learns the mount is unreachable (a live ASCOM Alpaca
    /// call throwing - network drop, mount powered off, Alpaca server gone). Unlike RASTA's
    /// equivalent, this doesn't cancel any in-progress capture or force navigation back to Prepare -
    /// SolScan's capture pipeline doesn't depend on mount state yet (that's Phase 5, "Automated
    /// acquisition"). Fires on MountService's own background polling thread, so everything here is
    /// marshaled onto the UI thread first.
    /// </summary>
    private void OnMountConnectionLost(Exception ex)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_serviceProvider is null)
                return;

            _serviceProvider.GetRequiredService<ITelescopeMount>().MarkDisconnected();
            _serviceProvider.GetRequiredService<MountState>().IsConnected = false;

            MessageBox.Show(
                $"The connection to the telescope mount was lost:\n\n{ex.Message}\n\n" +
                "The mount has been marked as disconnected. Check the mount/Alpaca connection and " +
                "reconnect from Prepare when ready.",
                "Telescope Connection Lost",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        });
    }
}
