namespace SolScan.Core.Capture;

/// <summary>
/// App-wide settings, not tied to any particular camera model - see
/// <see cref="Camera.ICameraSettingsStore"/> for that (Gain/Exposure/USB Turbo dial-in, remembered
/// per camera). Currently just where recordings are saved.
/// </summary>
/// <param name="CapturesRootFolder">Where SolScan writes new .ser recordings - null (the default)
/// means "use SolScan's own default location" (see <see cref="CaptureLocations.DefaultCapturesRootFolder"/>),
/// rather than baking that default path into the saved settings file itself, so a future change to
/// the default is picked up automatically for anyone who's never customized this.</param>
public sealed record AppSettings(string? CapturesRootFolder);

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
