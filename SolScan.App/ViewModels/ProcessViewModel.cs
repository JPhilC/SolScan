using CommunityToolkit.Mvvm.ComponentModel;

namespace SolScan.App.ViewModels;

/// <summary>
/// Placeholder for the Process stage: background SHG reconstruction pipeline, review/export.
/// Fleshed out in Phase 6/7 of SolScan CLAUDE.md.
/// </summary>
public partial class ProcessViewModel : ObservableObject
{
    [ObservableProperty]
    private string statusText = "Process - SHG reconstruction pipeline goes here.";
}
