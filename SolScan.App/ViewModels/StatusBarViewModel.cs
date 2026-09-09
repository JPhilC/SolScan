using CommunityToolkit.Mvvm.ComponentModel;

namespace SolScan.App.ViewModels;

/// <summary>
/// Shared, app-wide status bar - mirrors RASTA's StatusBarViewModel/StatusBar.xaml pattern (a
/// singleton reflecting cross-cutting state, docked full-width at the bottom of MainWindow, with
/// whichever stage view model is currently active pushing updates into it). Starts with just the
/// Capture view's live frame rate; more sections (mount status, etc.) can follow the same pattern
/// once those exist - see SolScan CLAUDE.md's phased build plan.
/// </summary>
public partial class StatusBarViewModel : ObservableObject
{
    [ObservableProperty]
    private string captureFrameRateText = "Capture: --";
}
