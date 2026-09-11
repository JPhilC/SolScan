namespace SolScan.Core.Telescope;

/// <summary>
/// Abstraction over an ASCOM-driven mount (Alpaca REST or COM - the implementation in
/// SolScan.Infrastructure decides). Deliberately mirrors the shape RASTA's ITelescopeMount/
/// AscomTelescopeMount already proved out for a different instrument - slewing, tracking and
/// site geometry don't care what's riding on the mount.
/// </summary>
public interface ITelescopeMount
{
    bool IsConnected { get; }
    bool IsTracking { get; }

    double SiteLatitudeDeg { get; }
    double SiteLongitudeDeg { get; }
    double SiteElevationM { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the mount locally disconnected without any live I/O - used when a caller has already
    /// learned (e.g. a live poll throwing) that the link is down, so a graceful DisconnectAsync()
    /// round-trip would just hang or fail again.
    /// </summary>
    void MarkDisconnected();

    /// <summary>Current mount pointing, right ascension in hours and declination in degrees (JNow).</summary>
    Task<(double RaHours, double DecDeg)> GetCurrentPositionAsync(CancellationToken cancellationToken = default);

    /// <summary>Slews to the given RA/Dec (JNow) and waits for the slew to complete. Implementations
    /// switch tracking on first if it isn't already - most mounts refuse (or silently no-op) an
    /// equatorial slew while tracking is off, the same as a person would just switch it on by hand
    /// before a goto.</summary>
    Task SlewToCoordinatesAsync(double raHours, double decDeg, CancellationToken cancellationToken = default);

    /// <summary>
    /// Syncs the mount's own pointing model to the given RA/Dec (JNow) - "tell the mount it is
    /// currently pointed here" - without any physical motion, unlike <see cref="SlewToCoordinatesAsync"/>.
    /// Used by SolScan.App.ViewModels.CaptureViewModel's "Find Sun" flow after a camera-based
    /// fine-tune: the Sun's ephemeris position is precisely known (see SolScan.Core.Astronomy.SunPosition),
    /// so centring it in the live view and syncing to that known position is a quick, always-available
    /// substitute for a manual star-alignment routine.
    /// </summary>
    Task SyncToCoordinatesAsync(double raHours, double decDeg, CancellationToken cancellationToken = default);

    Task SetTrackingAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Live-queries whether the mount is currently tracking, also updating <see cref="IsTracking"/>.</summary>
    Task<bool> GetTrackingAsync(CancellationToken cancellationToken = default);

    Task<bool> GetSlewingAsync(CancellationToken cancellationToken = default);
    Task<bool> GetAtParkAsync(CancellationToken cancellationToken = default);

    Task ParkAsync(CancellationToken cancellationToken = default);
    Task UnparkAsync(CancellationToken cancellationToken = default);

    // Manual hand control (see SolScan.App.ViewModels.HandControlViewModel) - the standard ASCOM
    // "hand paddle" primitives, distinct from SlewToCoordinatesAsync's goto-and-wait shape.

    /// <summary>The mount's own maximum axis slew rate in degrees/sec, queried live (Alpaca's
    /// AxisRates) rather than assumed - implementations fall back to a sensible default if the mount
    /// doesn't report anything usable. Used to scale a hand control's coarse speed levels.</summary>
    Task<double> GetMaxSlewRateDegPerSecAsync(CancellationToken cancellationToken = default);

    /// <summary>Continuous motion on one axis at a signed rate (degrees/sec) - 0 stops motion on
    /// that axis. This is the ASCOM/Alpaca hand-paddle primitive itself (<c>MoveAxis</c>), not a
    /// goto.</summary>
    Task MoveAxisAsync(TelescopeAxis axis, double rateDegPerSec, CancellationToken cancellationToken = default);

    /// <summary>Immediately stops any slew/axis motion in progress.</summary>
    Task AbortSlewAsync(CancellationToken cancellationToken = default);

    // Site management - editable independently of a live connection (SolScan's own Prepare-stage
    // site settings), and pushed to a connected mount so the two stay reconciled. See
    // SolScan.App.ViewModels.PrepareViewModel for the reconcile-on-connect flow.
    Task SetSiteLatitudeAsync(double latitudeDeg, CancellationToken cancellationToken = default);
    Task SetSiteLongitudeAsync(double longitudeDeg, CancellationToken cancellationToken = default);
    Task SetSiteElevationAsync(double elevationM, CancellationToken cancellationToken = default);
}
