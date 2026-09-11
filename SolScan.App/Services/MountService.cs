using System.Windows.Threading;
using SolScan.App.ViewModels;
using SolScan.Core.Telescope;

namespace SolScan.App.Services;

/// <summary>
/// Polls the connected mount for live position/status and pushes it into MountState/
/// StatusBarViewModel - trimmed port of RASTA's TelescopeService (RASTA.App.Services), dropped down
/// to SolScan's equatorial-only fields (no Az/Alt/Mode/Home).
/// </summary>
public sealed class MountService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ITelescopeMount _mount;
    private readonly MountState _state;
    private readonly StatusBarViewModel _statusBar;
    private readonly Dispatcher _dispatcher;

    private CancellationTokenSource? _cts;

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    /// <summary>
    /// Raised when a live poll call throws - the only signal available that the mount has actually
    /// gone away (network drop, mount powered off, Alpaca server gone); ITelescopeMount.IsConnected
    /// is just a cached flag set on ConnectAsync/DisconnectAsync, never re-derived from a live
    /// check, so this event is what App.xaml.cs's mount-disconnect recovery hooks into. Fires on
    /// this poll loop's own background thread - subscribers must marshal onto the UI thread
    /// themselves.
    /// </summary>
    public event Action<Exception>? ConnectionLost;

    public MountService(ITelescopeMount mount, MountState state, StatusBarViewModel statusBar)
    {
        _mount = mount;
        _state = state;
        _statusBar = statusBar;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public void Start()
    {
        if (!_mount.IsConnected)
        {
            _dispatcher.BeginInvoke(() => _statusBar.MountStatusText = "Mount: Disconnected");
            return;
        }

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var (raHours, decDeg) = await _mount.GetCurrentPositionAsync(cts.Token);
                    var isTracking = await _mount.GetTrackingAsync(cts.Token);
                    var isSlewing = await _mount.GetSlewingAsync(cts.Token);
                    var isParked = await _mount.GetAtParkAsync(cts.Token);

                    // Discarded deliberately - this is a fire-and-forget UI marshal, not something
                    // the poll loop should wait on before its next tick.
                    _ = _dispatcher.BeginInvoke(() =>
                    {
                        _state.RightAscensionHours = raHours;
                        _state.DeclinationDeg = decDeg;
                        _state.IsTracking = isTracking;
                        _state.IsSlewing = isSlewing;
                        _state.IsParked = isParked;

                        _statusBar.MountCoordinateText = $"RA: {raHours:F2}h  Dec: {decDeg:F2}°";
                        _statusBar.MountStatusText = "Mount: " + (isSlewing
                            ? "Slewing"
                            : isTracking
                                ? "Tracking"
                                : isParked
                                    ? "Parked"
                                    : "Connected");
                    });
                }
                catch (Exception ex)
                {
                    // A live mount call failing is the only way this app can tell the mount has
                    // actually gone away (see ConnectionLost above) - stop polling rather than
                    // retrying forever against a dead link.
                    _ = _dispatcher.BeginInvoke(() => _statusBar.MountStatusText = $"Mount: Error - {ex.Message}");
                    cts.Cancel();
                    ConnectionLost?.Invoke(ex);
                    break;
                }

                try
                {
                    await Task.Delay(PollInterval, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    public void Stop()
    {
        _cts?.Cancel();
        _dispatcher.BeginInvoke(() => _statusBar.MountStatusText = _mount.IsConnected ? "Mount: Connected" : "Mount: Disconnected");
    }
}
