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
/// <see cref="CaptureMetadata"/>.</param>
/// <param name="CaptureSettingsExpanded">Whether CaptureView.xaml's "Capture Settings" (colour
/// space/binning/ROI) Expander is open - purely a UI convenience remembered across sessions/
/// navigating away and back (CaptureViewModel is transient, so it can't just keep this in memory
/// itself), all defaulting to true (open) so a settings file saved before these existed still
/// deserializes into "nothing collapsed" rather than a UI that looks like it lost its own controls.</param>
/// <param name="CameraSettingsExpanded">Same idea, "Camera Settings" (Gain/Exposure/USB Turbo).</param>
/// <param name="HistogramExpanded">Same idea, the Histogram panel.</param>
/// <param name="DisplaySettingsExpanded">Same idea, "Display Settings" (Contrast black/white point +
/// Display Brightness).</param>
/// <param name="FocusAidExpanded">Same idea, the "Focus Aid" panel (collimator-focus edge-steepness
/// readout - see <see cref="Camera.FocusAnalyzer"/>).</param>
/// <param name="ReticuleExpanded">Same idea, the "Reticule" panel (crosshair + rotation-guide overlay
/// toggles/angle/inset - see CaptureView.xaml.cs's ReticuleOverlay).</param>
/// <param name="ShowCrosshairReticule">Whether the fixed (non-zoom-scaling) horizontal/vertical
/// crosshair overlay is drawn over the live preview - off by default, same "don't clutter the view
/// until asked" stance as <see cref="IsContrastAuto"/>.</param>
/// <param name="ShowRotationReticule">Whether the two vertical, pivotable rotation-guide lines
/// (inset from the preview's left/right edges) are drawn - see <see cref="ReticuleAngleDegrees"/>/
/// <see cref="ReticuleInsetPixels"/>. Off by default, same rationale as <see cref="ShowCrosshairReticule"/>.</param>
/// <param name="ReticuleAngleDegrees">How far the two rotation-guide lines are pivoted from vertical,
/// about each line's own midpoint - degrees, -10 to 10. Used to judge camera rotation by matching the
/// slit's two edges to these lines.</param>
/// <param name="ReticuleInsetPixels">How far in from the preview viewport's left/right edges the two
/// rotation-guide lines sit, in on-screen pixels (not scaled by zoom, same as the lines themselves).</param>
/// <param name="ProcessOptionsPanelExpanded">Whether ProcessView.xaml's right-hand panel (Process
/// Parameters/Image Enhancement/Image Selection, moved there from Options) is docked open or collapsed
/// to give the image preview the full window width - same "remembered across sessions" rationale as
/// <see cref="CaptureSettingsExpanded"/>, defaulting to true (open) for the same reason.</param>
/// <param name="ProcessParametersExpanded">Whether that panel's own "Process Parameters" Expander is
/// open. Same idea as <see cref="CaptureSettingsExpanded"/>, just for the Process view's panel.</param>
/// <param name="ProcessImageEnhancementExpanded">Same idea, the "Image Enhancement" Expander.</param>
/// <param name="ProcessImageSelectionExpanded">Same idea, the "Image Selection" Expander.</param>
/// <param name="CaptureOptionsPanelExpanded">Whether CaptureView.xaml's own right-hand drawer (Capture
/// Settings/Camera Settings/Histogram/Focus Aid/Reticule/Display Settings - Start/Stop Recording and
/// the frame counts stay on the main view, not in the drawer) is open - same idea as
/// <see cref="ProcessOptionsPanelExpanded"/>, just for Capture's own drawer.</param>
public sealed record AppSettings(
    string? CapturesRootFolder,
    string? AlpacaBaseUrl = null,
    int AlpacaDeviceNumber = 0,
    double SiteLatitudeDeg = 0,
    double SiteLongitudeDeg = 0,
    double SiteElevationM = 0,
    Guid? SelectedEquipmentSetupId = null,
    bool CaptureSettingsExpanded = true,
    bool CameraSettingsExpanded = true,
    bool HistogramExpanded = true,
    bool DisplaySettingsExpanded = true,
    bool FocusAidExpanded = true,
    bool ReticuleExpanded = true,
    bool ShowCrosshairReticule = false,
    bool ShowRotationReticule = false,
    double ReticuleAngleDegrees = 0,
    double ReticuleInsetPixels = 60,
    bool ProcessOptionsPanelExpanded = true,
    bool ProcessParametersExpanded = true,
    bool ProcessImageEnhancementExpanded = true,
    bool ProcessImageSelectionExpanded = true,
    bool CaptureOptionsPanelExpanded = true);

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
