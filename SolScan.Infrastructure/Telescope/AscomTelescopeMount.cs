using System.Globalization;
using SolScan.Core.Telescope;

namespace SolScan.Infrastructure.Telescope;

/// <summary>
/// ASCOM Alpaca (REST, via the ASCOM Remote Server) implementation of ITelescopeMount - see
/// SolScan CLAUDE.md "Scope for v1" for why this is Alpaca-only, not direct COM. Adapted from
/// RASTA's AscomTelescopeMount (RASTA.Infrastructure.Telescope): equatorial-only (no coordinate-mode
/// detection/AltAz slewing - SolScan's ITelescopeMount has no such surface), and
/// SlewToCoordinatesAsync now waits out the slew itself (see WaitForSlewCompleteAsync) rather than
/// leaving that to the caller, matching the interface's own doc comment.
/// </summary>
public class AscomTelescopeMount : ITelescopeMount
{
    // Generous cap for the short lead-offset slews a spectroheliograph capture actually needs
    // (see SolScan CLAUDE.md Phase 3's "find the sun") - not tuned against any real mount yet.
    private static readonly TimeSpan SlewTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan SlewPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly AscomAlpacaClient _client;
    private int _deviceNumber;

    public bool IsConnected { get; private set; }
    public bool IsTracking { get; private set; }

    public double SiteLatitudeDeg { get; private set; }
    public double SiteLongitudeDeg { get; private set; }
    public double SiteElevationM { get; private set; }

    public AscomTelescopeMount(AscomAlpacaClient client)
    {
        _client = client;
    }

    // -------------------------
    // Session configuration - not part of ITelescopeMount; SolScan.App.ViewModels.PrepareViewModel
    // calls these against the concrete type (same "if (_mount is AscomTelescopeMount m)" idiom
    // RASTA's SettingsViewModel uses) before connecting, using the Alpaca base URL/device number
    // from Options > General.
    // -------------------------

    public void SetBaseUrl(string baseUrl)
    {
        // baseUrl is like "http://127.0.0.1:11111/api/v1/telescope" - the device number is
        // appended when building endpoint URLs.
        _client.BaseUrl = $"{baseUrl}/{_deviceNumber}";
    }

    public void SetDeviceNumber(int deviceNumber)
    {
        _deviceNumber = deviceNumber;

        if (!string.IsNullOrWhiteSpace(_client.BaseUrl))
        {
            var idx = _client.BaseUrl.LastIndexOf('/');
            if (idx > 0)
            {
                var root = _client.BaseUrl[..idx];
                _client.BaseUrl = $"{root}/{_deviceNumber}";
            }
        }
    }

    // -------------------------
    // Connection
    // -------------------------

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _client.PutAsync("connected", cancellationToken, ("Connected", "true"));
        IsConnected = true;

        SiteLatitudeDeg = await _client.GetAsync<double>("sitelatitude", cancellationToken);
        SiteLongitudeDeg = await _client.GetAsync<double>("sitelongitude", cancellationToken);
        SiteElevationM = await _client.GetAsync<double>("siteelevation", cancellationToken);
        IsTracking = await _client.GetAsync<bool>("tracking", cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _client.PutAsync("connected", cancellationToken, ("Connected", "false"));
        IsConnected = false;
    }

    public void MarkDisconnected() => IsConnected = false;

    // -------------------------
    // Position / slewing
    // -------------------------

    public async Task<(double RaHours, double DecDeg)> GetCurrentPositionAsync(CancellationToken cancellationToken = default)
    {
        var raHours = await _client.GetAsync<double>("rightascension", cancellationToken);
        var decDeg = await _client.GetAsync<double>("declination", cancellationToken);
        return (raHours, decDeg);
    }

    public async Task SlewToCoordinatesAsync(double raHours, double decDeg, CancellationToken cancellationToken = default)
    {
        await _client.PutAsync("slewtocoordinates", cancellationToken,
            ("RightAscension", raHours.ToString(CultureInfo.InvariantCulture)),
            ("Declination", decDeg.ToString(CultureInfo.InvariantCulture)));

        await WaitForSlewCompleteAsync(cancellationToken);
    }

    /// <summary>Polls "slewing" until it clears, bounded by <see cref="SlewTimeout"/> - the
    /// interface's own SlewToCoordinatesAsync doc comment promises the slew is complete by the time
    /// it returns, unlike RASTA where the caller (PrepareViewModel there) polled this itself.</summary>
    private async Task WaitForSlewCompleteAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(SlewTimeout);

        try
        {
            while (await _client.GetAsync<bool>("slewing", timeoutCts.Token))
            {
                await Task.Delay(SlewPollInterval, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // cancellationToken itself wasn't cancelled - this was our own timeout firing.
            throw new TimeoutException($"Slew did not complete within {SlewTimeout.TotalSeconds:0}s.");
        }
    }

    // -------------------------
    // Tracking / park
    // -------------------------

    public async Task SetTrackingAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _client.PutAsync("tracking", cancellationToken, ("Tracking", enabled ? "true" : "false"));
        IsTracking = enabled;
    }

    public async Task<bool> GetTrackingAsync(CancellationToken cancellationToken = default)
    {
        IsTracking = await _client.GetAsync<bool>("tracking", cancellationToken);
        return IsTracking;
    }

    public Task<bool> GetSlewingAsync(CancellationToken cancellationToken = default) =>
        _client.GetAsync<bool>("slewing", cancellationToken);

    public Task<bool> GetAtParkAsync(CancellationToken cancellationToken = default) =>
        _client.GetAsync<bool>("atpark", cancellationToken);

    public Task ParkAsync(CancellationToken cancellationToken = default) =>
        _client.PutAsync("park", cancellationToken);

    public Task UnparkAsync(CancellationToken cancellationToken = default) =>
        _client.PutAsync("unpark", cancellationToken);

    // -------------------------
    // Site management
    // -------------------------

    public async Task SetSiteLatitudeAsync(double latitudeDeg, CancellationToken cancellationToken = default)
    {
        await _client.PutAsync("sitelatitude", cancellationToken, ("SiteLatitude", latitudeDeg.ToString(CultureInfo.InvariantCulture)));
        SiteLatitudeDeg = latitudeDeg;
    }

    public async Task SetSiteLongitudeAsync(double longitudeDeg, CancellationToken cancellationToken = default)
    {
        await _client.PutAsync("sitelongitude", cancellationToken, ("SiteLongitude", longitudeDeg.ToString(CultureInfo.InvariantCulture)));
        SiteLongitudeDeg = longitudeDeg;
    }

    public async Task SetSiteElevationAsync(double elevationM, CancellationToken cancellationToken = default)
    {
        await _client.PutAsync("siteelevation", cancellationToken, ("SiteElevation", elevationM.ToString(CultureInfo.InvariantCulture)));
        SiteElevationM = elevationM;
    }
}
