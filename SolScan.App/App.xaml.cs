using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SolScan.App.ViewModels;
using SolScan.Core.Equipment;
using SolScan.Infrastructure.Equipment;

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
        // TODO (Phase 2+): register ITelescopeMount / ICameraDevice / ISerWriter
        // implementations from SolScan.Infrastructure here once they exist.

        services.AddSingleton<IEquipmentLibrary, JsonEquipmentLibrary>();

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
