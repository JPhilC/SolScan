using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SolScan.App.ViewModels;

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
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // TODO (Phase 2+): register ITelescopeMount / ICameraDevice / ISerWriter
        // implementations from SolScan.Infrastructure here once they exist.

        services.AddSingleton<NavigationViewModel>();
        services.AddTransient<PrepareViewModel>();
        services.AddTransient<CaptureViewModel>();
        services.AddTransient<ProcessViewModel>();
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
