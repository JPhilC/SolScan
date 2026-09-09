using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace SolScan.App.ViewModels;

/// <summary>
/// Root nav view model bound to MainWindow. Resolves each stage's view model from the DI
/// container on demand - same NavigateTo&lt;TViewModel&gt; shape as RASTA's NavigationService,
/// kept this simple deliberately since there's no need for a routing framework here either.
/// </summary>
public partial class NavigationViewModel : ObservableObject
{
    /// <summary>
    /// Mirrors RASTA's NavigationViewModel.NavigationSection - drives which sidebar button is
    /// highlighted as "current" in MainWindow.xaml's DataTrigger styling.
    /// </summary>
    public enum NavigationSection
    {
        Prepare,
        Capture,
        Process,
        Options
    }

    private readonly IServiceProvider _serviceProvider;

    [ObservableProperty]
    private NavigationSection currentSection = NavigationSection.Prepare;

    [ObservableProperty]
    private object? currentViewModel;

    /// <summary>Bound as the StatusBar UserControl's DataContext in MainWindow.xaml - same
    /// shape as RASTA's NavigationViewModel.StatusBarViewModel.</summary>
    public StatusBarViewModel StatusBarViewModel { get; }

    public NavigationViewModel(IServiceProvider serviceProvider, StatusBarViewModel statusBarViewModel)
    {
        _serviceProvider = serviceProvider;
        StatusBarViewModel = statusBarViewModel;
    }

    public void NavigateTo<TViewModel>() where TViewModel : class
    {
        CurrentViewModel = _serviceProvider.GetRequiredService<TViewModel>();
    }

    [RelayCommand]
    private void NavigatePrepare()
    {
        CurrentSection = NavigationSection.Prepare;
        NavigateTo<PrepareViewModel>();
    }

    [RelayCommand]
    private void NavigateCapture()
    {
        CurrentSection = NavigationSection.Capture;
        NavigateTo<CaptureViewModel>();
    }

    [RelayCommand]
    private void NavigateProcess()
    {
        CurrentSection = NavigationSection.Process;
        NavigateTo<ProcessViewModel>();
    }

    [RelayCommand]
    private void NavigateOptions()
    {
        CurrentSection = NavigationSection.Options;
        NavigateTo<OptionsViewModel>();
    }
}
