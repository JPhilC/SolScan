using System.Globalization;
using System.Linq;
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

    // Fallback used by GetMaxSlewRateDegPerSecAsync when the mount's own AxisRates can't be read -
    // GSServer's own default maximum slew rate setting (SkySettings.MaximumSlewRate), reused here so
    // a hand control still has a sane speed scale even against a mount that doesn't report one.
    private const double DefaultMaxSlewRateDegPerSec = 3.5;

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
        // Most mounts refuse (or silently no-op) an equatorial SlewToCoordinates while tracking is
        // off - confirmed the hard way via CaptureViewModel.FindSunAsync landing a mount motionless
        // at its previous position. Rather than push that quirk onto every caller, switch tracking
        // on here first if it isn't already - matching what a person driving the mount by hand would
        // just do anyway before a goto.
        if (!await GetTrackingAsync(cancellationToken))
        {
            await SetTrackingAsync(true, cancellationToken);
        }

        await _client.PutAsync("slewtocoordinates", cancellationToken,
            ("RightAscension", raHours.ToString(CultureInfo.InvariantCulture)),
            ("Declination", decDeg.ToString(CultureInfo.InvariantCulture)));

        await WaitForSlewCompleteAsync(cancellationToken);
    }

    /// <summary>Alpaca's "synctocoordinates" - instantaneous, no polling to wait out (unlike
    /// <see cref="SlewToCoordinatesAsync"/>, which physically moves the mount).</summary>
    public Task SyncToCoordinatesAsync(double raHours, double decDeg, CancellationToken cancellationToken = default) =>
        _client.PutAsync("synctocoordinates", cancellationToken,
            ("RightAscension", raHours.ToString(CultureInfo.InvariantCulture)),
            ("Declination", decDeg.ToString(CultureInfo.InvariantCulture)));

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

    // -------------------------
    // Manual hand control
    // -------------------------

    /// <summary>Queries the Primary (RA) axis's AxisRates - a list of {Minimum, Maximum} deg/sec
    /// ranges the mount reports it supports - and returns the largest Maximum across them. Falls
    /// back to <see cref="DefaultMaxSlewRateDegPerSec"/> on any failure, or if the mount reports
    /// nothing usable (an empty list, or every range topping out at 0).</summary>
    public async Task<double> GetMaxSlewRateDegPerSecAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ranges = await _client.GetAsync<List<AxisRateRange>>("axisrates", cancellationToken, ("Axis", "0"));
            var max = ranges is { Count: > 0 } ? ranges.Max(r => r.Maximum) : 0;
            return max > 0 ? max : DefaultMaxSlewRateDegPerSec;
        }
        catch
        {
            // The mount not reporting AxisRates usably shouldn't block hand control entirely - fall
            // back to a sane default speed scale instead.
            return DefaultMaxSlewRateDegPerSec;
        }
    }

    public Task MoveAxisAsync(TelescopeAxis axis, double rateDegPerSec, CancellationToken cancellationToken = default) =>
        _client.PutAsync("moveaxis", cancellationToken,
            ("Axis", ((int)axis).ToString(CultureInfo.InvariantCulture)),
            ("Rate", rateDegPerSec.ToString(CultureInfo.InvariantCulture)));

    public Task AbortSlewAsync(CancellationToken cancellationToken = default) =>
        _client.PutAsync("abortslew", cancellationToken);

    /// <summary>Alpaca's AxisRates JSON shape - a list of {Minimum, Maximum} deg/sec range pairs for
    /// one axis (many mounts just report a single range spanning their full jog-speed capability).</summary>
    private sealed class AxisRateRange
    {
        public double Minimum { get; set; }
        public double Maximum { get; set; }
    }
}
