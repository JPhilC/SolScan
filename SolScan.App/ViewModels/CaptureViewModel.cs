using CommunityToolkit.Mvvm.ComponentModel;

namespace SolScan.App.ViewModels;

/// <summary>
/// Placeholder for the Capture stage: find the sun, slew ahead, auto start/stop SER recording.
/// Fleshed out in Phase 3/4/5 of SolScan CLAUDE.md.
/// </summary>
public partial class CaptureViewModel : ObservableObject
{
    [ObservableProperty]
    private string statusText = "Capture - slew/track/record goes here.";
}
