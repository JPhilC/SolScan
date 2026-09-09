using CommunityToolkit.Mvvm.ComponentModel;

namespace SolScan.App.ViewModels;

/// <summary>
/// Placeholder for the Prepare stage: connect the mount + camera, site settings.
/// Fleshed out in Phase 2/3 of SolScan CLAUDE.md.
/// </summary>
public partial class PrepareViewModel : ObservableObject
{
    [ObservableProperty]
    private string statusText = "Prepare - mount and camera connection goes here.";
}
