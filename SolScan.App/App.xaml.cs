using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SolScan.App.ViewModels;
using SolScan.Core.Camera;
using SolScan.Core.Capture;
using SolScan.Core.Equipment;
using SolScan.Infrastructure.Camera;
using SolScan.Infrastructure.Camera.Altair;
using SolScan.Infrastructure.Camera.Asi;
using SolScan.Infrastructure.Capture;
using SolScan.Infrastructure.Equipment;
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
        // TODO (Phase 2+): register ITelescopeMount from SolScan.Infrastructure once it exists.

        services.AddSingleton<IEquipmentLibrary, JsonEquipmentLibrary>();

        // One ICameraProvider per vendor (plus the hardware-free simulator) - aggregated by
        // ICameraDiscoveryService for the Capture view's camera picker. Each provider degrades to
        // an empty Discover() if its native SDK DLL isn't present - see
        // SolScan.Infrastructure/ASICamera2.README.md / altaircam.README.md.
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

        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<NavigationViewModel>();
        services.AddTransient<PrepareViewModel>();
        services.AddTransient<CaptureViewModel>();
        services.AddTransient<ProcessViewModel>();
        services.AddTransient<OptionsViewModel>();
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
