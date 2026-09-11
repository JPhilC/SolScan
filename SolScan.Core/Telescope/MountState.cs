using CommunityToolkit.Mvvm.ComponentModel;

namespace SolScan.Core.Telescope;

/// <summary>
/// Live, poll-refreshed mount status shared across the app - trimmed version of RASTA's
/// TelescopeState (no AltAz/Az/Alt fields, since SolScan is equatorial-only per CLAUDE.md "Scope
/// for v1"). Registered as a DI singleton: SolScan.App.Services.MountService writes to it every
/// poll tick, PrepareViewModel/StatusBarViewModel read it reactively via PropertyChanged.
/// </summary>
public partial class MountState : ObservableObject
{
    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private double rightAscensionHours;

    [ObservableProperty]
    private double declinationDeg;

    [ObservableProperty]
    private double siteLatitudeDeg;

    [ObservableProperty]
    private double siteLongitudeDeg;

    [ObservableProperty]
    private double siteElevationM;

    [ObservableProperty]
    private bool isTracking;

    [ObservableProperty]
    private bool isSlewing;

    [ObservableProperty]
    private bool isParked;

    /// <summary>Set once at connect time, read at disconnect time to decide whether to offer
    /// parking again - not itself observable, since nothing needs to react to it changing.</summary>
    public bool WasParkedOnConnect { get; set; }
}
