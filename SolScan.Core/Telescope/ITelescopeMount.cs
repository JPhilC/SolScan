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

    /// <summary>Current mount pointing, right ascension in hours and declination in degrees (JNow).</summary>
    Task<(double RaHours, double DecDeg)> GetCurrentPositionAsync(CancellationToken cancellationToken = default);

    /// <summary>Slews to the given RA/Dec (JNow) and waits for the slew to complete.</summary>
    Task SlewToCoordinatesAsync(double raHours, double decDeg, CancellationToken cancellationToken = default);

    Task SetTrackingAsync(bool enabled, CancellationToken cancellationToken = default);

    Task ParkAsync(CancellationToken cancellationToken = default);
    Task UnparkAsync(CancellationToken cancellationToken = default);
}
