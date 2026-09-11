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

    /// <summary>Slews to the given RA/Dec (JNow) and waits for the slew to complete.</summary>
    Task SlewToCoordinatesAsync(double raHours, double decDeg, CancellationToken cancellationToken = default);

    Task SetTrackingAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Live-queries whether the mount is currently tracking, also updating <see cref="IsTracking"/>.</summary>
    Task<bool> GetTrackingAsync(CancellationToken cancellationToken = default);

    Task<bool> GetSlewingAsync(CancellationToken cancellationToken = default);
    Task<bool> GetAtParkAsync(CancellationToken cancellationToken = default);

    Task ParkAsync(CancellationToken cancellationToken = default);
    Task UnparkAsync(CancellationToken cancellationToken = default);

    // Site management - editable independently of a live connection (SolScan's own Prepare-stage
    // site settings), and pushed to a connected mount so the two stay reconciled. See
    // SolScan.App.ViewModels.PrepareViewModel for the reconcile-on-connect flow.
    Task SetSiteLatitudeAsync(double latitudeDeg, CancellationToken cancellationToken = default);
    Task SetSiteLongitudeAsync(double longitudeDeg, CancellationToken cancellationToken = default);
    Task SetSiteElevationAsync(double elevationM, CancellationToken cancellationToken = default);
}
