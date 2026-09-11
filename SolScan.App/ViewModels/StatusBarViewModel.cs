using CommunityToolkit.Mvvm.ComponentModel;

namespace SolScan.App.ViewModels;

/// <summary>
/// Shared, app-wide status bar - mirrors RASTA's StatusBarViewModel/StatusBar.xaml pattern (a
/// singleton reflecting cross-cutting state, docked full-width at the bottom of MainWindow, with
/// whichever stage view model is currently active pushing updates into it). Started with just the
/// Capture view's live frame rate; SolScan.App.Services.MountService now pushes the mount's
/// connection/tracking/slewing/parked status and live RA/Dec here too, once a second.
/// </summary>
public partial class StatusBarViewModel : ObservableObject
{
    [ObservableProperty]
    private string captureFrameRateText = "Capture: --";

    [ObservableProperty]
    private string mountStatusText = "Mount: Disconnected";

    [ObservableProperty]
    private string mountCoordinateText = "RA: --  Dec: --";
}
