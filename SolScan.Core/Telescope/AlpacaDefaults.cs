namespace SolScan.Core.Telescope;

/// <summary>SolScan's own defaults for connecting to an ASCOM Alpaca telescope device, absent any
/// user customization - see <see cref="Capture.AppSettings.AlpacaBaseUrl"/>. Mirrors
/// <see cref="Capture.CaptureLocations"/>'s role for recording locations.</summary>
public static class AlpacaDefaults
{
    /// <summary>The standard local ASCOM Remote Server (Alpaca) endpoint for a telescope device,
    /// matching RASTA's own default.</summary>
    public const string DefaultBaseUrl = "http://127.0.0.1:11111/api/v1/telescope";

    public const int DefaultDeviceNumber = 0;
}
