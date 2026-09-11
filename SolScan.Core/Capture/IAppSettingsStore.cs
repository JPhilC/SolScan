namespace SolScan.Core.Capture;

/// <summary>
/// App-wide settings, not tied to any particular camera model - see
/// <see cref="Camera.ICameraSettingsStore"/> for that (Gain/Exposure/USB Turbo dial-in, remembered
/// per camera). Covers where recordings are saved, how to reach the ASCOM Alpaca mount, and the
/// site location Prepare's mount connection reconciles against - see each parameter's own doc
/// comment. All the mount-related fields default via optional parameters (rather than requiring
/// every caller to supply them) so the two pre-existing <c>new AppSettings(null)</c> "nothing saved
/// yet" call sites keep compiling unchanged.
/// </summary>
/// <param name="CapturesRootFolder">Where SolScan writes new .ser recordings - null (the default)
/// means "use SolScan's own default location" (see <see cref="CaptureLocations.DefaultCapturesRootFolder"/>),
/// rather than baking that default path into the saved settings file itself, so a future change to
/// the default is picked up automatically for anyone who's never customized this.</param>
/// <param name="AlpacaBaseUrl">The ASCOM Alpaca REST endpoint for the telescope device (e.g.
/// "http://127.0.0.1:11111/api/v1/telescope", without a trailing device number) - null (the
/// default) means <see cref="Telescope.AlpacaDefaults.DefaultBaseUrl"/>, same null-means-default
/// convention as <see cref="CapturesRootFolder"/>.</param>
/// <param name="AlpacaDeviceNumber">Which telescope device the Alpaca server exposes at that base
/// URL - almost always 0.</param>
/// <param name="SiteLatitudeDeg">Degrees, editable independently of whether a mount is connected -
/// see <see cref="Telescope.MountState"/> and PrepareViewModel's connect-time reconcile flow. Plain
/// (non-nullable), defaulting to 0 - there's no sensible "unset" sentinel distinct from a real
/// 0°/0° location.</param>
/// <param name="SiteLongitudeDeg">Degrees - see <see cref="SiteLatitudeDeg"/>.</param>
/// <param name="SiteElevationM">Metres - see <see cref="SiteLatitudeDeg"/>.</param>
/// <param name="SelectedEquipmentSetupId">The <see cref="Equipment.EquipmentSetup"/> (SHG+telescope
/// combo) currently picked on Prepare - null means nothing's been picked yet. Read fresh by
/// CaptureViewModel when a recording starts, to snapshot into that recording's
/// <see cref="CaptureEquipmentMetadata"/>.</param>
public sealed record AppSettings(
    string? CapturesRootFolder,
    string? AlpacaBaseUrl = null,
    int AlpacaDeviceNumber = 0,
    double SiteLatitudeDeg = 0,
    double SiteLongitudeDeg = 0,
    double SiteElevationM = 0,
    Guid? SelectedEquipmentSetupId = null);

/// <summary>
/// Persists <see cref="AppSettings"/> - implemented by
/// <c>SolScan.Infrastructure.Capture.JsonAppSettingsStore</c>, one JSON file under
/// <c>%LocalAppData%\SolScan\</c>, mirroring <see cref="Camera.ICameraSettingsStore"/>'s own
/// JSON-file pattern: per-machine data, not something that should roam to another machine.
/// </summary>
public interface IAppSettingsStore
{
    /// <summary>Never returns null itself - an unset/missing/corrupt settings file just yields
    /// <c>new AppSettings(null)</c>, same as <see cref="Camera.ICameraSettingsStore"/>'s "nothing
    /// saved yet" case, since there's only ever one of these (no per-camera-model keying needed).</summary>
    AppSettings Load();

    void Save(AppSettings settings);
}
