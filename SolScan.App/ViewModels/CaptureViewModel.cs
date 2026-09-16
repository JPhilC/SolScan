using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SolScan.App.Services;
using SolScan.App.Views;
using SolScan.Core.Astronomy;
using SolScan.Core.Camera;
using SolScan.Core.Capture;
using SolScan.Core.Equipment;
using SolScan.Core.Processing;
using SolScan.Core.Telescope;
using SolScan.Processing.Spectrum;

namespace SolScan.App.ViewModels;

/// <summary>
/// Capture stage: pick a discovered camera, connect + live view it, tune gain/exposure/contrast,
/// and record to a .ser file - see SolScan CLAUDE.md Phase 4. Frames arrive off
/// <see cref="ICameraDevice.FrameCaptured"/> on whatever thread the connected device raises it on
/// (a dedicated poll thread for ZWO ASI, the vendor SDK's own callback thread for Altair - see
/// their respective device classes in SolScan.Infrastructure) - every frame is written to the SER
/// file synchronously on that thread when recording, while the preview bitmap/histogram redraw is
/// throttled and marshalled to the UI thread separately, so a fast camera can't flood the UI.
/// </summary>
public partial class CaptureViewModel : ObservableObject
{
    private static readonly TimeSpan PreviewRedrawInterval = TimeSpan.FromMilliseconds(50); // ~20fps cap
    private static readonly TimeSpan FrameRateUpdateInterval = TimeSpan.FromSeconds(1);

    // Live spectral-line overlay (see ProcessPreviewFrame) - deliberately slower than the preview
    // redraw itself: a person turning a grating by hand doesn't need 20fps responsiveness, and the
    // analysis (a real curvature fit + 12-candidate correlation over the frame's own full resolution -
    // 3840x2160 on real ASI678MM hardware) is real work worth not repeating on every single throttled
    // preview tick. NOT YET VALIDATED against real hardware timing - a starting value, not a measured one.
    private static readonly TimeSpan SpectralOverlayUpdateInterval = TimeSpan.FromMilliseconds(400);
    private DateTime _lastSpectralOverlayUtc = DateTime.MinValue;

    // "Find Sun" fine-tune (see FindSunAsync) - a simple brightness hill-climb, not the full
    // spiral-search-then-hill-climb algorithm CLAUDE.md's "Visual fine-centering" describes: the
    // ephemeris slew should already land the Sun somewhere in frame, so there's no need for a
    // from-zero-signal search phase for v1.
    //
    // UNTESTED against real hardware - the constants below are unvalidated first guesses (see
    // FindSunAsync's own doc comment) and may well need retuning once tried against a real mount +
    // camera pointed at the actual Sun.
    private const double FindSunCoarseNudgeFraction = 0.01; // ~1% of the mount's own max slew rate
    private const double FindSunFineNudgeFraction = 0.3; // second pass, relative to the coarse rate
    private static readonly TimeSpan FindSunPulseDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FindSunSettleDelay = TimeSpan.FromMilliseconds(400); // let a couple of throttled preview frames catch up
    private const int FindSunMaxStepsPerAxis = 8;
    // Relative, not absolute - Mono16's raw average sits ~256x higher than Mono8's for the same
    // scene, so a fixed absolute threshold would be far too twitchy in one format and far too
    // insensitive in the other.
    private const double FindSunRelativeImprovementThreshold = 0.005;
    private const double FindSunFallbackMaxSlewRateDegPerSec = 3.5; // matches AscomTelescopeMount's own hand-control fallback

    private readonly ICameraDiscoveryService _discoveryService;
    private readonly Func<ISerWriter> _serWriterFactory;
    private readonly ICameraSettingsStore _cameraSettingsStore;
    private readonly IAppSettingsStore _appSettingsStore;
    private readonly IEquipmentLibrary _equipmentLibrary;
    private readonly ICaptureMetadataStore _captureMetadataStore;
    private readonly ITelescopeMount _mount;
    private readonly MountState _mountState;
    private readonly Func<HandControlWindow> _handControlWindowFactory;
    private readonly StatusBarViewModel _statusBar;
    private readonly Dispatcher _dispatcher;
    private readonly Lock _recordingLock = new();

    /// <summary>Total-frame average sensor value from the most recently processed preview frame
    /// (see <see cref="ProcessPreviewFrame"/>) - the same total-brightness-as-slit-overlap proxy
    /// CLAUDE.md's "Visual fine-centering" note describes. Only ever read/written on the UI thread
    /// (set from <see cref="ProcessPreviewFrame"/>'s dispatcher callback, read from
    /// <see cref="FindSunAsync"/>'s own UI-thread async continuations), so no locking is needed.</summary>
    private double _lastFrameAverageBrightness;

    /// <summary>The currently-open Hand Control window, if any - tracked so a second click on
    /// "Hand Control…" brings the existing one to front instead of opening a duplicate (two windows
    /// independently sending MoveAxis would race each other).</summary>
    private HandControlWindow? _handControlWindow;

    private ICameraDevice? _connectedCamera;

    /// <summary>The CameraProfile resolved (auto-added or matched by name) for
    /// <see cref="_connectedCamera"/> - see the camera auto-add block in <see cref="ToggleLiveViewAsync"/>.
    /// Snapshotted into a recording's CaptureMetadata by <see cref="StartRecording"/>.</summary>
    private CameraProfile? _connectedCameraProfile;

    /// <summary>The SHG currently picked on Prepare's Equipment Setup, resolved once in the constructor
    /// and again at every camera connect (see <see cref="ResolveSpectrographProfile"/>) rather than
    /// re-read from <see cref="_appSettingsStore"/> on every throttled preview tick - feeds the live
    /// spectral overlay (<see cref="SolScan.Processing.Spectrum.SpectralOverlayAnalyzer"/> in
    /// <see cref="ProcessPreviewFrame"/>). Deliberately independent of the camera connection - it's
    /// purely an Equipment Setup concept, so unlike <see cref="_connectedCameraProfile"/> it's never
    /// cleared on disconnect, and stays available for <see cref="LoadTestImage"/> even when no
    /// camera has ever connected this session. Same "doesn't auto-refresh mid-session if Options
    /// changes it" limitation as <c>PrepareViewModel.AvailableEquipmentSetups</c> already accepts -
    /// null if nothing's picked.</summary>
    private SpectrographProfile? _connectedInstrument;

    private ISerWriter? _activeWriter;
    private bool _writerNeedsOpening;
    private DateTime _lastPreviewRedrawUtc = DateTime.MinValue;

    // The actual camera capture rate (every frame, not just the throttled ~20fps preview redraw
    // above) - reported on the shared StatusBarViewModel roughly once a second. Only ever touched
    // from the capture thread (each device raises FrameCaptured from a single dedicated thread of
    // its own), so plain fields are fine here.
    private int _frameArrivalCount;
    private DateTime _lastFrameRateUpdateUtc;

    // Guards against feedback loops: true while a property is being updated *from* the connected
    // device (readback while Auto is on, or the Exposure<->ExposureSliderPosition cross-link)
    // rather than by the user, so the corresponding OnXChanged doesn't push the same value
    // straight back to the camera or the other property.
    private bool _syncingFromDevice;
    private bool _syncingExposureLink;

    // Debounces PersistSettingsIfConnected - see that method's own doc comment for why.
    private DispatcherTimer? _persistSettingsDebounceTimer;

    // Debounces ApplyOutputFormatChange - see ScheduleApplyOutputFormatChange's own doc comment for why.
    private DispatcherTimer? _applyOutputFormatDebounceTimer;

    // Debounces OnCaptureOptionsPanelWidthChanged - see that method's own doc comment for why.
    private DispatcherTimer? _persistPanelWidthDebounceTimer;

    // 0 = idle, 1 = a background preview-processing Task is currently running - see
    // OnFrameCaptured/ProcessPreviewFrame. Interlocked rather than a plain bool since it's read and
    // written from whichever thread the connected device raises FrameCaptured on.
    private int _previewProcessingInFlight;

    // 0 = idle, 1 = a colour space/binning/ROI change is scheduled or in flight - see
    // ScheduleApplyOutputFormatChange/ApplyOutputFormatChange and OnFrameCaptured's own use of this
    // below (same Interlocked-not-plain-bool rationale as _previewProcessingInFlight: read from the
    // capture thread, written from the UI thread).
    private int _outputFormatChangePending;

    public ObservableCollection<ICameraDevice> AvailableCameras { get; } = [];

    [ObservableProperty]
    private ICameraDevice? selectedCamera;

    [ObservableProperty]
    private bool isConnected;

    /// <summary>Mirrors <see cref="MountState.IsConnected"/> - gates the "Hand Control…" button,
    /// since jogging the mount only makes sense once it's actually connected (see PrepareViewModel).</summary>
    [ObservableProperty]
    private bool isMountConnected;

    [ObservableProperty]
    private bool isLive;

    [ObservableProperty]
    private bool isRecording;

    /// <summary>True while <see cref="FindSunAsync"/> is running - gates re-entry and disables the
    /// camera/mount controls it relies on staying put mid-run (live view, disconnect, recording).</summary>
    [ObservableProperty]
    private bool isFindingSun;

    /// <summary>Raw gain, 0-600 (0.1dB/step) - the ASI678MM's own range, matching what SharpCap
    /// shows for it (see <see cref="ICameraDevice.Gain"/>).</summary>
    [ObservableProperty]
    private double gain = 150;

    [ObservableProperty]
    private bool isGainAuto;

    [ObservableProperty]
    private double exposureMicroseconds = 10_000;

    /// <summary>ASICap-style Exposure range dropdown options - see <see cref="ExposureScale.Ranges"/>.
    /// Selects which sub-range <see cref="ExposureDisplayValue"/> and CaptureView.xaml's Exposure
    /// Slider (bound directly to <see cref="ExposureMicroseconds"/>, with Minimum/Maximum bound to
    /// this range's own bounds) currently operate over, since exposure spans 0.032ms-5s and no
    /// single linear control can usefully cover that whole range at once.</summary>
    public IReadOnlyList<ExposureRangeOption> AvailableExposureRanges { get; } = ExposureScale.Ranges;

    [ObservableProperty]
    private ExposureRangeOption selectedExposureRange = ExposureScale.Ranges[0]; // must match AvailableExposureRanges[0]

    /// <summary>The Exposure numeric up/down box's own displayed value, in <see
    /// cref="SelectedExposureRange"/>'s unit (e.g. 8000 when its Unit is "µs", or 2.5 when it's
    /// "s") - kept in sync with <see cref="ExposureMicroseconds"/> in both directions via <see
    /// cref="_syncingExposureLink"/>.</summary>
    [ObservableProperty]
    private double exposureDisplayValue = 10_000;

    [ObservableProperty]
    private bool isExposureAuto;

    /// <summary>SharpCap-style "USB Turbo" throttle - see <see cref="ICameraDevice.UsbBandwidthPercent"/>.
    /// CaptureView.xaml's slider floors this at 40 (not 0) to match what SharpCap shows for the
    /// ASI678MM; Altair's device maps its own discrete speed levels into the same 0-100 space.</summary>
    [ObservableProperty]
    private int usbBandwidthPercent = 80;

    [ObservableProperty]
    private bool isUsbBandwidthAuto;

    /// <summary>SharpCap's "Colour Space" dropdown - see <see cref="ICameraDevice.OutputFormat"/>.</summary>
    public IReadOnlyList<CameraOutputFormat> AvailableColorSpaces { get; } = [CameraOutputFormat.Mono8, CameraOutputFormat.Mono16];

    [ObservableProperty]
    private CameraOutputFormat selectedColorSpace = CameraOutputFormat.Mono16;

    /// <summary>Populated from <see cref="ICameraDevice.SupportedBinning"/> once a camera connects
    /// - e.g. [1, 2, 3, 4] for the ASI678MM - rather than assuming every camera supports the same
    /// range.</summary>
    public ObservableCollection<int> AvailableBinningOptions { get; } = [1];

    [ObservableProperty]
    private int selectedBinning = 1;

    /// <summary>Desired ROI width, centred on the sensor - a real hardware setting applied via
    /// <see cref="ICameraDevice.SetOutputFormatAsync"/> (see <see cref="ApplyOutputFormatChange"/>),
    /// not a post-capture crop: the camera itself only reads out and transfers this region, which is
    /// what actually reduces USB bandwidth and raises achievable frame rate - confirmed on real
    /// ASI678MM hardware (an earlier, software-only-crop version of this feature left live-view fps
    /// unchanged, since the camera kept transferring the full frame regardless). 0 (the default)
    /// means "full frame": <see cref="OnFrameCaptured"/> fills this in with the actual sensor width
    /// the first time a frame arrives, purely so the textbox shows a real number instead of a bare
    /// "0" - functionally 0 and the sensor's own full width mean the same thing to the device.</summary>
    [ObservableProperty]
    private int roiWidth;

    /// <summary>See <see cref="RoiWidth"/> - same idea, sensor height.</summary>
    [ObservableProperty]
    private int roiHeight;

    /// <summary>Display-only contrast stretch (0-1, normalized to the frame's own bit depth) -
    /// never affects what's written to the SER file, see <see cref="FramePreview.Stretch"/>. Only
    /// meaningful when <see cref="IsContrastAuto"/> is off - while it's on, these are overwritten
    /// from <see cref="FramePreview.ComputeAutoStretch"/> every redraw instead (read-only from the
    /// UI's perspective, same as Gain/Exposure while their own Auto is on).</summary>
    [ObservableProperty]
    private double contrastBlackPoint;

    [ObservableProperty]
    private double contrastWhitePoint = 1;

    /// <summary>Off by default: dialing in real Gain/Exposure settings needs the live view to
    /// faithfully show the actual, unmodified exposure - the whole point of the exercise - not an
    /// auto-brightened stand-in for it (matches ASICap/SharpCap's own default). Available to turn
    /// on for just eyeballing a scene, where a typical narrow-dynamic-range scene otherwise looking
    /// far too dark/contrasty (see <see cref="FramePreview.ComputeAutoStretch"/>'s doc comment) is
    /// more of an annoyance than useful signal.</summary>
    [ObservableProperty]
    private bool isContrastAuto;

    /// <summary>Display-only gamma applied on top of the black/white stretch above (see
    /// <see cref="FramePreview.Stretch"/>'s <c>displayGamma</c> parameter) - never affects what's
    /// written to the SER file. Unlike the black/white points, this has no "Auto" counterpart: it's
    /// a purely cosmetic "make the live view easier to look at" knob, not something the camera or a
    /// histogram algorithm has an opinion on. Defaults to <see cref="FramePreview.DefaultDisplayGamma"/>
    /// (1, a no-op) so the preview starts out faithful to the actual exposure, same rationale as
    /// <see cref="IsContrastAuto"/> defaulting off.</summary>
    [ObservableProperty]
    private double displayBrightness = FramePreview.DefaultDisplayGamma;

    // Whether each CaptureView.xaml Expander is currently open - remembered across sessions (see
    // AppSettings.CaptureSettingsExpanded and friends) rather than per-camera-model, since these are
    // a UI layout preference with nothing to do with which camera is connected. Field initializers
    // here just match AppSettings' own "open by default" default; the constructor overwrites them
    // from whatever was actually saved before anything can observe the mismatch.
    [ObservableProperty]
    private bool isCaptureSettingsExpanded = true;

    [ObservableProperty]
    private bool isCameraSettingsExpanded = true;

    [ObservableProperty]
    private bool isHistogramExpanded = true;

    [ObservableProperty]
    private bool isDisplaySettingsExpanded = true;

    [ObservableProperty]
    private bool isFocusAidExpanded = true;

    [ObservableProperty]
    private bool isReticuleExpanded = true;

    /// <summary>Whether the right-hand drawer (Capture Settings/Camera Settings/Histogram/Focus Aid/
    /// Reticule/Display Settings) is open - Start/Stop Recording and the frame counts live outside it,
    /// directly on the main view, so they stay visible/usable regardless of this. See
    /// AppSettings.CaptureOptionsPanelExpanded and CaptureView.xaml's own doc comment for the mechanism
    /// (md:DrawerHost, same pattern ProcessView.xaml uses).</summary>
    [ObservableProperty]
    private bool isCaptureOptionsPanelExpanded = true;

    /// <summary>Whether the panel above is "pinned" - VS-tool-window style - into a real, resizable
    /// docked column (CaptureView.xaml's own code-behind, <c>UpdatePanelDockState</c>) instead of
    /// shown as md:DrawerHost's default floating overlay. Off by default (today's overlay-only
    /// behavior unchanged) - see <see cref="IsCaptureDrawerOpen"/>/<see cref="IsCaptureOptionsPanelDocked"/>
    /// for how this and <see cref="IsCaptureOptionsPanelExpanded"/> combine to pick one or the other.</summary>
    [ObservableProperty]
    private bool isCaptureOptionsPanelPinned;

    /// <summary>The docked column's width in pixels while pinned - set from CaptureView.xaml.cs
    /// whenever the user drags its GridSplitter (see <c>OptionsPanelHost.SizeChanged</c>), persisted
    /// (debounced, see <see cref="OnCaptureOptionsPanelWidthChanged"/>) the same way a GridSplitter-
    /// resized column normally isn't. Meaningless while unpinned - CaptureView.xaml.cs only ever
    /// applies it to the docked column's own width, never the floating drawer's fixed one.</summary>
    [ObservableProperty]
    private double captureOptionsPanelWidth = 340;

    /// <summary>Drives <c>md:DrawerHost.IsRightDrawerOpen</c> - true only while the panel is open
    /// <em>and not</em> pinned, since a pinned-open panel is shown docked instead of as a floating
    /// overlay (see <see cref="IsCaptureOptionsPanelDocked"/>). The setter writes straight through to
    /// <see cref="IsCaptureOptionsPanelExpanded"/> - needed because DrawerHost's own binding is
    /// <c>Mode=TwoWay</c> (e.g. Escape/scrim-click closes it) - which is always correct here: the
    /// drawer can only have been open because <see cref="IsCaptureOptionsPanelPinned"/> was already
    /// false, so collapsing <see cref="IsCaptureOptionsPanelExpanded"/> is exactly what closing it
    /// should do.</summary>
    public bool IsCaptureDrawerOpen
    {
        get => IsCaptureOptionsPanelExpanded && !IsCaptureOptionsPanelPinned;
        set => IsCaptureOptionsPanelExpanded = value;
    }

    /// <summary>Drives the docked column/GridSplitter's visibility in CaptureView.xaml - true only
    /// while the panel is both open and pinned (see <see cref="IsCaptureDrawerOpen"/> for the
    /// complementary floating-overlay case).</summary>
    public bool IsCaptureOptionsPanelDocked => IsCaptureOptionsPanelExpanded && IsCaptureOptionsPanelPinned;

    /// <summary>Fixed (non-zoom-scaling) horizontal/vertical crosshair overlay - drawn by
    /// CaptureView.xaml.cs's ReticuleOverlay directly over the preview viewport, not inside the
    /// zoomed/scrolled Image itself, so it always renders as thin on-screen lines regardless of
    /// <see cref="SelectedZoomOption"/>. Off by default, same "don't clutter the view until asked"
    /// stance as <see cref="IsContrastAuto"/>.</summary>
    [ObservableProperty]
    private bool showCrosshairReticule;

    /// <summary>Two vertical, pivotable rotation-guide lines inset from the preview viewport's left/
    /// right edges - lets the user judge camera rotation by matching the slit's two edges to these
    /// lines (see <see cref="ReticuleAngleDegrees"/>). Same non-zoom-scaling overlay as
    /// <see cref="ShowCrosshairReticule"/>, off by default.</summary>
    [ObservableProperty]
    private bool showRotationReticule;

    /// <summary>How far the two rotation-guide lines are pivoted from vertical, about each line's own
    /// midpoint (see CaptureView.xaml.cs's CreatePivotedVerticalLine) - degrees, -10 to 10.</summary>
    [ObservableProperty]
    private double reticuleAngleDegrees;

    /// <summary>How far in from the preview viewport's left/right edges the two rotation-guide lines
    /// sit, in on-screen pixels - not scaled by zoom, same as the lines themselves.</summary>
    [ObservableProperty]
    private double reticuleInsetPixels = 60;

    /// <summary>Live overlay labelling the 12 named <see cref="SpectralRay"/> lines currently visible
    /// in the preview - see <see cref="SpectralLineLabels"/>/<see cref="ProcessPreviewFrame"/>. On by
    /// default, unlike the Reticule overlays above - see <see cref="AppSettings.ShowSpectralLineLabels"/>'s
    /// own doc comment for why.</summary>
    [ObservableProperty]
    private bool showSpectralLineLabels = true;

    /// <summary>Coloured gradient band on the preview's left edge showing what part of the visible
    /// spectrum the current view spans - see <see cref="SpectralGradientStops"/>. Same on-by-default
    /// rationale as <see cref="ShowSpectralLineLabels"/>.</summary>
    [ObservableProperty]
    private bool showSpectralColorBand = true;

    /// <summary>Every named line currently visible in the preview, already positioned in preview-
    /// bitmap pixel space - see <see cref="SpectralLineLabel"/>'s own doc comment for the remaining
    /// bitmap-space-to-control-space step CaptureView.xaml.cs still does. Updated from
    /// <see cref="ProcessPreviewFrame"/>'s own throttled spectral-overlay step, not every preview
    /// frame - see that method's own comment.</summary>
    [ObservableProperty]
    private ObservableCollection<SpectralLineLabel> spectralLineLabels = [];

    /// <summary>The colour band's own gradient stops, sampled across the currently visible wavelength
    /// range via <see cref="SpectralColor.ToRgb"/> - null until the first successful spectral-overlay
    /// pass (see <see cref="ProcessPreviewFrame"/>), e.g. before any instrument/camera is resolved or
    /// while nothing could be identified/scored at all.</summary>
    [ObservableProperty]
    private GradientStopCollection? spectralGradientStops;

    [ObservableProperty]
    private bool isSpectralOverlayExpanded = true;

    /// <summary>Pixel size the spectral overlay falls back to when no connected camera's own
    /// <see cref="CameraProfile.PixelSizeMicrons"/> is known - see <see cref="AppSettings.SpectralOverlayFallbackPixelSizeMicrons"/>'s
    /// own doc comment. Editable directly (a plain <c>TextBox</c>, not read from hardware) since a
    /// loaded test image isn't tied to any real camera the way live values elsewhere on this view are.</summary>
    [ObservableProperty]
    private double spectralOverlayFallbackPixelSizeMicrons = 2.0;

    /// <summary>Raw numbers behind the current overlay - best-guess ray/score/confidence, the
    /// curvature-detected centre row, dispersion, and how many named lines came out visible - shown in
    /// the "Spectral Overlay" Expander so an unexpected label position/colour can be checked against
    /// real values instead of guessed at from what's on screen (same reasoning as <c>SolScan.Tools</c>'
    /// own <c>annotate</c> console output). Updated on the same throttled cadence as the overlay
    /// itself; null until the first successful pass.</summary>
    [ObservableProperty]
    private string? spectralOverlayDiagnosticsText;

    [ObservableProperty]
    private WriteableBitmap? previewBitmap;

    /// <summary>SharpCap-style Zoom dropdown options - three "fit to available space" modes plus a
    /// fixed set of percentages matching SharpCap's own list exactly. See <see cref="ZoomOption"/>'s
    /// doc comment for why the actual pixel-size computation from whichever of these is selected
    /// lives in CaptureView.xaml.cs rather than here.</summary>
    public IReadOnlyList<ZoomOption> AvailableZoomOptions { get; } =
    [
        new("Auto", ZoomKind.Auto),
        new("Fit Width", ZoomKind.FitWidth),
        new("Fit Height", ZoomKind.FitHeight),
        new("16%", ZoomKind.Fixed, 16),
        new("20%", ZoomKind.Fixed, 20),
        new("25%", ZoomKind.Fixed, 25),
        new("33%", ZoomKind.Fixed, 33),
        new("40%", ZoomKind.Fixed, 40),
        new("50%", ZoomKind.Fixed, 50),
        new("66%", ZoomKind.Fixed, 66),
        new("75%", ZoomKind.Fixed, 75),
        new("100%", ZoomKind.Fixed, 100),
        new("125%", ZoomKind.Fixed, 125),
        new("150%", ZoomKind.Fixed, 150),
        new("175%", ZoomKind.Fixed, 175),
        new("200%", ZoomKind.Fixed, 200),
        new("250%", ZoomKind.Fixed, 250),
        new("300%", ZoomKind.Fixed, 300),
        new("400%", ZoomKind.Fixed, 400),
        new("600%", ZoomKind.Fixed, 600),
        new("800%", ZoomKind.Fixed, 800),
    ];

    [ObservableProperty]
    private ZoomOption selectedZoomOption = new("Auto", ZoomKind.Auto); // must match AvailableZoomOptions[0] exactly (record struct equality) for the ComboBox to show it selected initially

    /// <summary>A filled silhouette over <see cref="FramePreview.HistogramBucketCount"/> x [0,1]
    /// - CaptureView.xaml just stretches it into whatever panel space it has (a Viewbox), so no
    /// converter/per-bucket item template is needed for what's otherwise a 256-element collection
    /// redrawn ~20 times a second.</summary>
    [ObservableProperty]
    private Geometry? histogramGeometry;

    /// <summary>Raw sensor value range/average actually seen in the current frame, plus its
    /// reported bit depth - the same numbers ASICap's own histogram panel shows (Max/Min/AVG).
    /// Not just a nicety: it's the fastest way to tell whether an unexpected preview exposure is a
    /// display-stretch problem or the camera's own reported values for a given
    /// <see cref="SelectedColorSpace"/> being different than expected - see
    /// <see cref="FramePreview.ComputeHistogramStats"/>'s doc comment.</summary>
    [ObservableProperty]
    private string histogramStatsText = string.Empty;

    /// <summary>Collimator-focus aid readout - see <see cref="FocusAnalyzer.MeasureEdgeSteepness"/>.
    /// The number to *minimize* while adjusting the collimator: a sharper disk/slit edge transitions
    /// over fewer pixels. Unlike a raw gradient-magnitude metric, this is a physical distance (in
    /// pixels) that stays comparable across different Gain/Exposure settings on the same Binning/ROI
    /// - it's meant to be watched the same way sunscan-app's own equivalent readout is, just with the
    /// opposite "smaller is better" direction (the same convention astrophotography autofocus routines
    /// use for HFD/HFR).</summary>
    [ObservableProperty]
    private string edgeWidthText = "—";

    /// <summary>Running low-water mark of <see cref="EdgeWidthText"/>'s underlying pixel value since
    /// the live view was last (re)started or <see cref="ResetBestEdgeWidthCommand"/> was last pressed
    /// - see <see cref="_bestEdgeWidthPixels"/> and sunscan-app's own "(Best: …)" readout (CLAUDE.md's
    /// sunscan-app entry) this mirrors: lets the user dial the collimator back and forth past the true
    /// optimum and still see the best (narrowest) point actually reached, rather than only the
    /// current, possibly-already-past-optimum reading.</summary>
    [ObservableProperty]
    private string bestEdgeWidthText = "—";

    /// <summary>Backing value for <see cref="BestEdgeWidthText"/> - the raw (not display-formatted)
    /// low-water mark itself, in pixels, so each new reading can be compared against it directly.
    /// <see cref="double.PositiveInfinity"/> means "nothing recorded yet" (so the very first
    /// measurement always counts as a new best). Only ever touched from the UI thread (set from
    /// <see cref="ProcessPreviewFrame"/>'s dispatcher callback, reset from the UI-thread-only
    /// <see cref="ResetBestEdgeWidth"/>/<see cref="ToggleLiveViewAsync"/>), same threading rationale
    /// as <see cref="_lastFrameAverageBrightness"/> above.</summary>
    private double _bestEdgeWidthPixels = double.PositiveInfinity;

    /// <summary>How many recent valid <see cref="FocusAnalyzer.MeasureEdgeSteepness"/> readings are
    /// kept for <see cref="SmoothEdgeWidth"/>'s rolling median - see its own doc comment. Small enough
    /// that a real focus change still shows up within a few preview frames (well under a second at
    /// the ~20fps preview redraw rate).</summary>
    private const int RecentEdgeWidthWindowSize = 5;

    /// <summary>Backing store for <see cref="SmoothEdgeWidth"/>'s rolling median - only ever touched
    /// from the UI thread, same threading rationale as <see cref="_bestEdgeWidthPixels"/>.</summary>
    private readonly List<double> _recentEdgeWidthsPixels = new(RecentEdgeWidthWindowSize);

    [ObservableProperty]
    private int frameCount;

    [ObservableProperty]
    private long droppedFrameCount;

    [ObservableProperty]
    private string? recordingFilePath;

    [ObservableProperty]
    private string statusText = "Pick a camera, then press Connect/Play to start the live view.";

    public CaptureViewModel(
        ICameraDiscoveryService discoveryService,
        Func<ISerWriter> serWriterFactory,
        ICameraSettingsStore cameraSettingsStore,
        IAppSettingsStore appSettingsStore,
        IEquipmentLibrary equipmentLibrary,
        ICaptureMetadataStore captureMetadataStore,
        ITelescopeMount mount,
        MountState mountState,
        Func<HandControlWindow> handControlWindowFactory,
        StatusBarViewModel statusBar)
    {
        _discoveryService = discoveryService;
        _serWriterFactory = serWriterFactory;
        _cameraSettingsStore = cameraSettingsStore;
        _appSettingsStore = appSettingsStore;
        _equipmentLibrary = equipmentLibrary;
        _captureMetadataStore = captureMetadataStore;
        _mount = mount;
        _mountState = mountState;
        _handControlWindowFactory = handControlWindowFactory;
        _statusBar = statusBar;
        _dispatcher = Dispatcher.CurrentDispatcher;

        IsMountConnected = _mountState.IsConnected;
        _mountState.PropertyChanged += MountStatePropertyChanged;

        // Bypasses the properties' own setters (and so their OnXChanged save-back-to-disk logic) -
        // this is CaptureViewModel reading its own previously-saved state, not the user toggling an
        // Expander, so there's nothing to persist yet. Same rationale as the _syncingFromDevice-
        // guarded blocks elsewhere in this constructor's callees, just simpler here since there's no
        // device to keep in sync with - a straight field assignment is enough.
        var savedAppSettings = _appSettingsStore.Load();
        isCaptureSettingsExpanded = savedAppSettings.CaptureSettingsExpanded;
        isCameraSettingsExpanded = savedAppSettings.CameraSettingsExpanded;
        isHistogramExpanded = savedAppSettings.HistogramExpanded;
        isDisplaySettingsExpanded = savedAppSettings.DisplaySettingsExpanded;
        isFocusAidExpanded = savedAppSettings.FocusAidExpanded;
        isReticuleExpanded = savedAppSettings.ReticuleExpanded;
        isCaptureOptionsPanelExpanded = savedAppSettings.CaptureOptionsPanelExpanded;
        isCaptureOptionsPanelPinned = savedAppSettings.CaptureOptionsPanelPinned;
        captureOptionsPanelWidth = savedAppSettings.CaptureOptionsPanelWidth;
        showCrosshairReticule = savedAppSettings.ShowCrosshairReticule;
        showRotationReticule = savedAppSettings.ShowRotationReticule;
        reticuleAngleDegrees = savedAppSettings.ReticuleAngleDegrees;
        reticuleInsetPixels = savedAppSettings.ReticuleInsetPixels;
        showSpectralLineLabels = savedAppSettings.ShowSpectralLineLabels;
        showSpectralColorBand = savedAppSettings.ShowSpectralColorBand;
        isSpectralOverlayExpanded = savedAppSettings.SpectralOverlayExpanded;
        spectralOverlayFallbackPixelSizeMicrons = savedAppSettings.SpectralOverlayFallbackPixelSizeMicrons;

        // Resolved here too, not just at camera connect (see _connectedInstrument's own doc comment) -
        // the SHG doesn't actually depend on a camera being connected at all, and the live spectral
        // overlay needs it available even when testing with a loaded still image (LoadTestImage)
        // with no camera connected.
        _connectedInstrument = ResolveSpectrographProfile();

        RefreshCameras();
    }

    private void MountStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MountState.IsConnected))
        {
            IsMountConnected = _mountState.IsConnected;
        }
    }

    partial void OnIsMountConnectedChanged(bool value)
    {
        OpenHandControlCommand.NotifyCanExecuteChanged();
        FindSunCommand.NotifyCanExecuteChanged();
        SyncMountCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Opens the pop-out, modeless Hand Control window (see Views/HandControlWindow.xaml) -
    /// brings the existing one to front instead of opening a second if one's already open, since two
    /// would independently race each other sending MoveAxis commands.</summary>
    [RelayCommand(CanExecute = nameof(IsMountConnected))]
    private void OpenHandControl()
    {
        if (_handControlWindow is not null)
        {
            _handControlWindow.Activate();
            return;
        }

        _handControlWindow = _handControlWindowFactory();
        _handControlWindow.Closed += (_, _) => _handControlWindow = null;
        _handControlWindow.Show();
    }

    private bool CanToggleLiveView => SelectedCamera is not null && !IsRecording && !IsFindingSun;
    private bool CanDisconnect => IsConnected && !IsRecording && !IsFindingSun;
    private bool CanStartRecording => IsLive && !IsRecording && !IsFindingSun;
    private bool CanFindSun => IsMountConnected && !IsRecording && !IsFindingSun;
    private bool CanSyncMount => IsMountConnected && !IsFindingSun;

    /// <summary>
    /// Manual counterpart to <see cref="FindSunAsync"/>'s own end-of-flow sync offer: for when the
    /// user has aligned the Sun in the live preview themselves (e.g. via Hand Control) rather than
    /// through the automatic fine-tune, and just wants to sync the mount's pointing model to that
    /// now-correct alignment. Syncs to a freshly computed ephemeris position, same reasoning as
    /// <see cref="SyncMountToSunPositionAsync"/>'s own doc comment.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSyncMount))]
    private async Task SyncMountAsync()
    {
        try
        {
            var (raHours, decDeg) = await SyncMountToSunPositionAsync();
            StatusText = $"Synced mount to today's computed Sun position (RA {raHours:F3}h, Dec {decDeg:F2}°).";
        }
        catch (Exception ex)
        {
            StatusText = $"Sync failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Syncs the mount to a *freshly* computed ephemeris position (not one computed earlier and
    /// reused, since even a short delay lets the Sun's real position drift by a meaningful amount -
    /// see <see cref="FindSunAsync"/>'s own call site) and deliberately not
    /// <see cref="ITelescopeMount.GetCurrentPositionAsync"/>'s readback: syncing the mount to its own
    /// existing belief about where it's pointed would be a no-op, since that belief already differs
    /// from the truth by whatever error a sync exists to correct.
    /// </summary>
    private async Task<(double RaHours, double DecDeg)> SyncMountToSunPositionAsync()
    {
        var (raHours, decDeg) = SunPosition.GetApparentRaDecJNow(DateTime.UtcNow);
        await _mount.SyncToCoordinatesAsync(raHours, decDeg);
        return (raHours, decDeg);
    }

    /// <summary>
    /// "Find Sun" - see SolScan CLAUDE.md Phase 3. Slews the mount to today's computed solar
    /// position (<see cref="SunPosition"/>), then - only if a camera is live here - offers to
    /// fine-tune pointing using the live view's total frame brightness as a slit-overlap proxy
    /// (<see cref="RunFineTuneAsync"/>), then offers to sync the mount's pointing model to the
    /// result via Alpaca. No camera live, or the user declines the fine-tune, and the flow stops
    /// right after the ephemeris slew - there's nothing more automatic to offer without a camera to
    /// judge alignment by.
    ///
    /// NOT YET VERIFIED against real hardware: the ephemeris slew + tracking-on fix + manual Sync
    /// button have been (see CLAUDE.md's "Also real" note), but the camera-driven fine-tune itself
    /// (<see cref="RunFineTuneAsync"/>/<see cref="ClimbAxisAsync"/>) has only been exercised by
    /// build/unit tests, not against a real mount+camera pointed at the actual Sun - in particular
    /// the nudge rate/pulse duration/settle delay constants near the top of this class are
    /// unvalidated guesses, and may need retuning once it's actually tried.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFindSun))]
    private async Task FindSunAsync()
    {
        try
        {
            IsFindingSun = true;

            var (raHours, decDeg) = SunPosition.GetApparentRaDecJNow(DateTime.UtcNow);

            StatusText = $"Find Sun: slewing to RA {raHours:F3}h, Dec {decDeg:F2}°…";
            await _mount.SlewToCoordinatesAsync(raHours, decDeg);
            StatusText = $"Find Sun: slewed to the computed position (RA {raHours:F3}h, Dec {decDeg:F2}°).";

            if (!(IsLive && _connectedCamera is not null))
            {
                StatusText += " No live camera - stopping here.";
                return;
            }

            var fineTune = MessageBox.Show(
                "Slewed to the Sun's computed position. Fine-tune pointing now using the live camera view?",
                "Find Sun",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;

            if (!fineTune)
            {
                StatusText = "Find Sun: done (camera fine-tune skipped).";
                return;
            }

            StatusText = "Find Sun: fine-tuning using the live camera view…";
            var improved = await RunFineTuneAsync();
            StatusText = improved
                ? "Find Sun: fine-tune improved centring on the live view."
                : "Find Sun: fine-tune made no further improvement (already well centred).";

            var doSync = MessageBox.Show(
                "Sync the mount's pointing to this position via Alpaca now?",
                "Find Sun",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;

            if (!doSync)
            {
                StatusText = "Find Sun: complete.";
                return;
            }

            var (syncRaHours, syncDecDeg) = await SyncMountToSunPositionAsync();
            StatusText = $"Find Sun: mount synced to RA {syncRaHours:F3}h, Dec {syncDecDeg:F2}°.";
        }
        catch (Exception ex)
        {
            StatusText = $"Find Sun failed: {ex.Message}";
        }
        finally
        {
            IsFindingSun = false;
        }
    }

    /// <summary>Two coarse-then-fine hill-climb passes over RA then Dec - see
    /// <see cref="ClimbAxisAsync"/>. Returns whether any pass actually improved brightness.</summary>
    private async Task<bool> RunFineTuneAsync()
    {
        double maxRateDegPerSec;
        try
        {
            maxRateDegPerSec = await _mount.GetMaxSlewRateDegPerSecAsync();
        }
        catch
        {
            // GetMaxSlewRateDegPerSecAsync already falls back internally on failure (see its own
            // doc comment) - reaching here means something else entirely went wrong; fall back the
            // same way HandControlViewModel does rather than aborting the whole fine-tune.
            maxRateDegPerSec = FindSunFallbackMaxSlewRateDegPerSec;
        }

        var coarseRate = maxRateDegPerSec * FindSunCoarseNudgeFraction;
        var fineRate = coarseRate * FindSunFineNudgeFraction;

        var improved = await ClimbAxisAsync(TelescopeAxis.Primary, coarseRate);
        improved |= await ClimbAxisAsync(TelescopeAxis.Secondary, coarseRate);
        improved |= await ClimbAxisAsync(TelescopeAxis.Primary, fineRate);
        improved |= await ClimbAxisAsync(TelescopeAxis.Secondary, fineRate);
        return improved;
    }

    /// <summary>
    /// One-dimensional brightness hill-climb on a single mount axis: nudges in one direction while
    /// <see cref="_lastFrameAverageBrightness"/> keeps improving, backs off the final (non-improving)
    /// step so the axis ends up at the peak rather than one step past it, and tries the opposite
    /// direction first if the very first nudge didn't help at all. Bounded by
    /// <see cref="FindSunMaxStepsPerAxis"/> so a flat/noisy signal can't loop indefinitely.
    /// </summary>
    private async Task<bool> ClimbAxisAsync(TelescopeAxis axis, double rateDegPerSec)
    {
        var direction = 1.0;
        var best = _lastFrameAverageBrightness;

        var first = await NudgeAndMeasureAsync(axis, direction, rateDegPerSec);
        if (!IsBrighterThan(first, best))
        {
            // That direction didn't help - undo it and try the opposite one instead.
            await NudgeAndMeasureAsync(axis, -direction, rateDegPerSec);
            direction = -1.0;
            first = await NudgeAndMeasureAsync(axis, direction, rateDegPerSec);

            if (!IsBrighterThan(first, best))
            {
                // Neither direction helped - undo and give up on this axis for this pass.
                await NudgeAndMeasureAsync(axis, -direction, rateDegPerSec);
                return false;
            }
        }

        best = first;
        for (var step = 0; step < FindSunMaxStepsPerAxis; step++)
        {
            var after = await NudgeAndMeasureAsync(axis, direction, rateDegPerSec);
            if (!IsBrighterThan(after, best))
            {
                // Overshot the peak - undo this last step and stop.
                await NudgeAndMeasureAsync(axis, -direction, rateDegPerSec);
                break;
            }
            best = after;
        }

        return true;
    }

    private static bool IsBrighterThan(double after, double before) =>
        after > before * (1 + FindSunRelativeImprovementThreshold);

    /// <summary>Pulses <paramref name="axis"/> at <paramref name="rateDegPerSec"/> * <paramref name="direction"/>
    /// for <see cref="FindSunPulseDuration"/>, stops it, waits <see cref="FindSunSettleDelay"/> for a
    /// couple of throttled preview frames to catch up, then returns the freshly-measured brightness.</summary>
    private async Task<double> NudgeAndMeasureAsync(TelescopeAxis axis, double direction, double rateDegPerSec)
    {
        await _mount.MoveAxisAsync(axis, direction * rateDegPerSec);
        await Task.Delay(FindSunPulseDuration);
        await _mount.MoveAxisAsync(axis, 0);
        await Task.Delay(FindSunSettleDelay);
        return _lastFrameAverageBrightness;
    }

    [RelayCommand]
    private void RefreshCameras()
    {
        var previouslySelectedId = SelectedCamera?.Id;
        AvailableCameras.Clear();
        foreach (var camera in _discoveryService.DiscoverAll())
        {
            AvailableCameras.Add(camera);
        }

        SelectedCamera = AvailableCameras.FirstOrDefault(c => c.Id == previouslySelectedId)
            ?? AvailableCameras.FirstOrDefault();
    }

    /// <summary>
    /// Adds whatever's in <paramref name="supportedBinning"/> but not yet in
    /// <see cref="AvailableBinningOptions"/> - never removes anything, so the collection only ever
    /// grows here. Paired with <see cref="RemoveStaleBinningOptions"/>, called only once
    /// <see cref="SelectedBinning"/> has moved to a value in the new set: together they replace the
    /// dropdown's contents for a newly-connected camera without ever Clear()-ing it outright, which
    /// would momentarily leave <see cref="AvailableBinningOptions"/> empty while the bound
    /// ComboBox's SelectedItem still (briefly) has nothing to match - WPF's SelectedItem binding
    /// reacts to that by trying to push null back into <see cref="SelectedBinning"/> (a plain,
    /// non-nullable int), which throws and shows up as the ComboBox's default red validation-error
    /// adorner right on every connect/Play.
    /// </summary>
    private void AddMissingBinningOptions(IReadOnlyList<int> supportedBinning)
    {
        foreach (var binning in supportedBinning)
        {
            if (!AvailableBinningOptions.Contains(binning))
            {
                AvailableBinningOptions.Add(binning);
            }
        }
    }

    /// <summary>See <see cref="AddMissingBinningOptions"/> - removes whatever's in
    /// <see cref="AvailableBinningOptions"/> but not in <paramref name="supportedBinning"/>. Safe to
    /// call once <see cref="SelectedBinning"/> no longer references any of them.</summary>
    private void RemoveStaleBinningOptions(IReadOnlyList<int> supportedBinning)
    {
        for (var i = AvailableBinningOptions.Count - 1; i >= 0; i--)
        {
            if (!supportedBinning.Contains(AvailableBinningOptions[i]))
            {
                AvailableBinningOptions.RemoveAt(i);
            }
        }
    }

    /// <summary>The "connect/play" button - connects <see cref="SelectedCamera"/> if it isn't
    /// already, then starts streaming; a second press just stops streaming (stays connected).
    /// Deliberately distinct from <see cref="DisconnectAsync"/> - see CaptureView.xaml.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleLiveView))]
    private async Task ToggleLiveViewAsync()
    {
        if (SelectedCamera is null)
        {
            return;
        }

        if (IsLive)
        {
            await SelectedCamera.StopStreamingAsync();
            SelectedCamera.FrameCaptured -= OnFrameCaptured;
            IsLive = false;
            StatusText = "Live view stopped.";
            _statusBar.CaptureFrameRateText = "Capture: --";
            return;
        }

        if (_connectedCamera is not null && _connectedCamera != SelectedCamera)
        {
            await DisconnectCoreAsync();
        }

        if (!IsConnected)
        {
            await SelectedCamera.ConnectAsync();
            _connectedCamera = SelectedCamera;
            IsConnected = true;
            _connectedCameraProfile = ResolveCameraProfile(SelectedCamera);
            _connectedInstrument = ResolveSpectrographProfile();

            // SupportedBinning is camera-specific, so the dropdown is (re)populated on every
            // connect regardless of whether saved settings exist. Adds anything newly-supported
            // *before* SelectedBinning moves below, and only removes what's stale *after* - see
            // AddMissingBinningOptions's doc comment for why: naively Clear()-ing first would
            // momentarily empty the bound ComboBox's ItemsSource while SelectedBinning (a plain,
            // non-nullable int) still points at the old value, and WPF's SelectedItem binding
            // reacting to that by trying to push null back into a non-nullable int throws - showing
            // as the ComboBox's default red validation-error adorner right on every connect/Play.
            var supportedBinning = SelectedCamera.SupportedBinning;
            AddMissingBinningOptions(supportedBinning);

            // Remembered from a previous session with this same camera *model* (keyed by Name,
            // not Id - see ICameraSettingsStore) - if there's nothing saved yet, fall back to
            // reading the freshly-connected camera's own just-applied defaults instead, same as
            // before this existed.
            var savedSettings = _cameraSettingsStore.Load(SelectedCamera.Name);

            _syncingFromDevice = true;
            if (savedSettings is not null)
            {
                Gain = savedSettings.Gain;
                ExposureMicroseconds = savedSettings.ExposureMicroseconds;
                UsbBandwidthPercent = savedSettings.UsbBandwidthPercent;
                IsGainAuto = savedSettings.IsGainAuto;
                IsExposureAuto = savedSettings.IsExposureAuto;
                IsUsbBandwidthAuto = savedSettings.IsUsbBandwidthAuto;
                ContrastBlackPoint = savedSettings.ContrastBlackPoint;
                ContrastWhitePoint = savedSettings.ContrastWhitePoint;
                IsContrastAuto = savedSettings.IsContrastAuto;
                DisplayBrightness = savedSettings.DisplayBrightness;
                SelectedColorSpace = savedSettings.OutputFormat;
                // Falls back to the camera's own current binning if the saved value isn't (or is no
                // longer) one this camera actually supports, rather than selecting something invalid.
                SelectedBinning = supportedBinning.Contains(savedSettings.Binning) ? savedSettings.Binning : SelectedCamera.Binning;
                RoiWidth = savedSettings.RoiWidth;
                RoiHeight = savedSettings.RoiHeight;
            }
            else
            {
                SelectedColorSpace = SelectedCamera.OutputFormat;
                SelectedBinning = SelectedCamera.Binning;
                RoiWidth = 0;
                RoiHeight = 0;
            }
            _syncingFromDevice = false;

            // SelectedBinning now definitely points at something in supportedBinning, so anything
            // else left over from a previous camera can be dropped without ever removing the
            // currently-selected entry.
            RemoveStaleBinningOptions(supportedBinning);

            SelectedCamera.Gain = Gain;
            SelectedCamera.ExposureMicroseconds = ExposureMicroseconds;
            SelectedCamera.UsbBandwidthPercent = UsbBandwidthPercent;
            SelectedCamera.IsGainAuto = IsGainAuto;
            SelectedCamera.IsExposureAuto = IsExposureAuto;
            SelectedCamera.IsUsbBandwidthAuto = IsUsbBandwidthAuto;

            // Unconditional, not "if it differs from what the camera already reports": ROI has no
            // readback property to compare against (see ICameraDevice.SetOutputFormatAsync's doc
            // comment), and applying it again when it already matches is a harmless, connect-time-
            // only extra native call - far simpler than trying to know in advance whether a saved
            // RoiWidth/RoiHeight differs from whatever the camera just defaulted to on its own.
            await SelectedCamera.SetOutputFormatAsync(SelectedColorSpace, SelectedBinning, RoiWidth, RoiHeight);
        }

        _frameArrivalCount = 0;
        _lastFrameRateUpdateUtc = DateTime.UtcNow;
        ResetBestEdgeWidth(); // a "best" carried over from a previous live-view session isn't meaningful for this one
        SelectedCamera.FrameCaptured += OnFrameCaptured;
        await SelectedCamera.StartStreamingAsync();
        IsLive = true;
        StatusText = $"Live: {SelectedCamera.Name}";
    }

    /// <summary>Closes the camera entirely (stops streaming first if needed) rather than just
    /// pausing the feed - see <see cref="ToggleLiveViewAsync"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync() => await DisconnectCoreAsync();

    private async Task DisconnectCoreAsync()
    {
        if (_connectedCamera is null)
        {
            return;
        }

        // Cancel rather than let it fire after disconnect - it would just no-op anyway (see
        // ApplyOutputFormatChange's own null-camera guard), but there's no point applying a hardware
        // reconfiguration to a camera about to be disconnected. FlushPendingCameraSettings still
        // persists whatever RoiWidth/RoiHeight/etc. are currently set to regardless, since those
        // properties are already updated synchronously - only the actual device call was debounced.
        // Clearing _outputFormatChangePending here too since stopping the timer means
        // ApplyOutputFormatChange's own finally will now never run to clear it - left set, a later
        // reconnect on this same (transient, but reused-until-navigated-away) view model instance
        // would wrongly keep suppressing OnFrameCaptured's RoiWidth/RoiHeight display backfill.
        _applyOutputFormatDebounceTimer?.Stop();
        Interlocked.Exchange(ref _outputFormatChangePending, 0);
        FlushPendingCameraSettings();

        if (IsLive)
        {
            await _connectedCamera.StopStreamingAsync();
            _connectedCamera.FrameCaptured -= OnFrameCaptured;
            IsLive = false;
            _statusBar.CaptureFrameRateText = "Capture: --";
        }

        await _connectedCamera.DisconnectAsync();
        IsConnected = false;
        _connectedCamera = null;
        _connectedCameraProfile = null;
        // _connectedInstrument deliberately NOT cleared here - the SHG selection has nothing to do
        // with the camera connection (see its own doc comment), so it stays resolved for
        // LoadTestImage to keep using even with no camera ever connected this session.
        SpectralLineLabels = [];
        SpectralGradientStops = null;
        StatusText = "Camera disconnected.";
    }

    /// <summary>
    /// Looks up a <see cref="CameraProfile"/> matching <paramref name="camera"/>'s
    /// <see cref="ICameraDevice.Name"/> in the equipment library (case-insensitive - same "key by
    /// Name, not Id" reasoning as <see cref="ICameraSettingsStore"/>'s own per-camera-model
    /// settings), auto-adding one - filled in from the connected hardware - the first time this
    /// camera model is ever seen. If a matching entry already exists but is missing
    /// <see cref="CameraProfile.PixelSizeMicrons"/> while the device now reports one, backfills and
    /// saves it - never overwrites an existing non-null value, which may have been corrected by hand.
    /// </summary>
    private CameraProfile ResolveCameraProfile(ICameraDevice camera)
    {
        var cameras = _equipmentLibrary.LoadCameras();
        var existing = cameras.FirstOrDefault(c => string.Equals(c.Label, camera.Name, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            var added = new CameraProfile(Guid.NewGuid(), camera.Name, camera.PixelSizeMicrons);
            _equipmentLibrary.SaveCameras([.. cameras, added]);
            return added;
        }

        if (existing.PixelSizeMicrons is null && camera.PixelSizeMicrons is not null)
        {
            var backfilled = existing with { PixelSizeMicrons = camera.PixelSizeMicrons };
            _equipmentLibrary.SaveCameras(cameras.Select(c => c.Id == backfilled.Id ? backfilled : c).ToList());
            return backfilled;
        }

        return existing;
    }

    /// <summary>Not live/not recording - loading a test image while a real camera is actively
    /// streaming would just have the next real frame overwrite it almost immediately (confusing, not
    /// dangerous), and loading one mid-recording makes no sense at all (it would never reach the
    /// actual .ser file - live frames are written synchronously in OnFrameCaptured, not from here).</summary>
    private bool CanLoadTestImage => !IsLive && !IsRecording;

    /// <summary>
    /// Loads a PNG/TIFF file (see <see cref="TestImageLoader"/>) and feeds it through the exact same
    /// pipeline a live frame would go through (<see cref="ProcessPreviewFrame"/>: histogram, contrast
    /// stretch, focus aid, and - the actual point of this feature - the spectral line-label/colour-
    /// band overlay), so that overlay can be tried and tuned without a camera or telescope connected
    /// at all. A dev/test feature, not part of the normal capture workflow - see
    /// <see cref="_connectedInstrument"/>/<see cref="SpectralOverlayFallbackPixelSizeMicrons"/> for
    /// what it uses in place of a live camera's own resolved instrument/pixel size.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoadTestImage))]
    private void LoadTestImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a test image to load into the preview",
            Filter = "Image files (*.png;*.tif;*.tiff)|*.png;*.tif;*.tiff|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        CameraFrame frame;
        try
        {
            frame = TestImageLoader.Load(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            StatusText = $"Failed to load '{dialog.FileName}': {ex.Message}";
            return;
        }

        // A deliberate one-shot test action should show its result immediately, not wait out
        // whatever's left of the normal ~400ms spectral-overlay throttle window.
        _lastSpectralOverlayUtc = DateTime.MinValue;

        if (Interlocked.CompareExchange(ref _previewProcessingInFlight, 1, 0) != 0)
        {
            StatusText = "Still processing the previous preview frame - try again in a moment.";
            return;
        }

        StatusText = $"Test image loaded: {Path.GetFileName(dialog.FileName)}";
        Task.Run(() => ProcessPreviewFrame(frame, camera: null));
    }

    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    private void StartRecording()
    {
        if (SelectedCamera is null)
        {
            return;
        }

        // Read fresh (a cheap JSON file read, not hot-path) rather than cached at startup, so a
        // change made in Options > General takes effect on the very next recording without
        // requiring a restart. Falls back to SolScan's own default location - see
        // AppSettings.CapturesRootFolder's doc comment for why a customized value is used exactly
        // as chosen at this level (no further "SolScan\Captures" appended) - the one thing always
        // added underneath it, custom or default, is the date subfolder below.
        var rootFolder = _appSettingsStore.Load().CapturesRootFolder is { Length: > 0 } customFolder
            ? customFolder
            : CaptureLocations.DefaultCapturesRootFolder;

        // One timestamp for both the date subfolder and the filename, not two separate
        // DateTime.Now calls, so the two can never disagree across a midnight boundary.
        var now = DateTime.Now;
        var directory = Path.Combine(rootFolder, now.ToString("yyyyMMdd"));
        Directory.CreateDirectory(directory);
        RecordingFilePath = Path.Combine(directory, $"SolScan_{now:yyyyMMdd_HHmmss}.ser");

        WriteCaptureMetadata(RecordingFilePath);

        lock (_recordingLock)
        {
            _activeWriter = _serWriterFactory();
            // Width/height/bit depth come from the first captured frame rather than being guessed
            // here, so Open() is deferred to OnFrameCaptured.
            _writerNeedsOpening = true;
        }

        FrameCount = 0;
        IsRecording = true;
        StatusText = $"Recording to {RecordingFilePath}";
    }

    /// <summary>
    /// Snapshots whichever SHG/telescope (from the Equipment Setup picked on Prepare - see
    /// AppSettings.SelectedEquipmentSetupId), camera (see <see cref="_connectedCameraProfile"/>),
    /// camera dial-in settings (see <see cref="BuildCurrentCameraSettings"/>) and mount pointing (see
    /// <see cref="BuildMountPointingSnapshot"/>) are current right now into a
    /// <see cref="CaptureMetadata"/>, and writes it alongside the .ser file - so SolScan.Processing
    /// can read back what produced this recording later, even if the library entries/live state it
    /// was snapshotted from have since changed. Any field can end up null (e.g. no Equipment Setup
    /// ever picked, or the mount wasn't connected) - written anyway, rather than skipped, so a
    /// recording still gets a metadata file either way. <see cref="CaptureMetadata.StudiedRay"/> is
    /// always null for now - see that field's own doc comment for why.
    /// </summary>
    private void WriteCaptureMetadata(string serFilePath)
    {
        var selectedSetupId = _appSettingsStore.Load().SelectedEquipmentSetupId;
        var setup = selectedSetupId is { } id ? _equipmentLibrary.LoadSetups().FirstOrDefault(s => s.Id == id) : null;

        TelescopeProfile? telescope = setup is not null
            ? _equipmentLibrary.LoadTelescopes().FirstOrDefault(t => t.Id == setup.TelescopeProfileId)
            : null;

        _captureMetadataStore.Write(serFilePath, new CaptureMetadata(
            ResolveSpectrographProfile(setup),
            telescope,
            _connectedCameraProfile,
            DateTime.UtcNow,
            _connectedCamera is not null ? BuildCurrentCameraSettings() : null,
            BuildMountPointingSnapshot()));
    }

    /// <summary>The SHG currently picked as part of Prepare's Equipment Setup, or null if none/nothing's
    /// picked - shared by <see cref="WriteCaptureMetadata"/> (a fresh lookup each recording) and
    /// <see cref="_connectedInstrument"/> (resolved once per camera connect, for the live spectral
    /// overlay - see <see cref="ProcessPreviewFrame"/>). <paramref name="setup"/> is optional purely so
    /// <see cref="WriteCaptureMetadata"/>, which already looked one up for its own Telescope field,
    /// doesn't do the same <see cref="IAppSettingsStore.Load"/>/<see cref="IEquipmentLibrary.LoadSetups"/>
    /// work twice.</summary>
    private SpectrographProfile? ResolveSpectrographProfile(EquipmentSetup? setup = null)
    {
        if (setup is null)
        {
            var selectedSetupId = _appSettingsStore.Load().SelectedEquipmentSetupId;
            setup = selectedSetupId is { } id ? _equipmentLibrary.LoadSetups().FirstOrDefault(s => s.Id == id) : null;
        }

        return setup is not null
            ? _equipmentLibrary.LoadSpectrographs().FirstOrDefault(s => s.Id == setup.SpectrographProfileId)
            : null;
    }

    /// <summary>The mount's RA/Dec and site location right now, or null if it isn't connected - see
    /// <see cref="MountPointingSnapshot"/>'s own doc comment for why this reads <see cref="_mountState"/>
    /// (already poll-refreshed) rather than making a fresh Alpaca query.</summary>
    private MountPointingSnapshot? BuildMountPointingSnapshot() =>
        _mountState.IsConnected
            ? new MountPointingSnapshot(
                _mountState.RightAscensionHours,
                _mountState.DeclinationDeg,
                _mountState.SiteLatitudeDeg,
                _mountState.SiteLongitudeDeg,
                _mountState.SiteElevationM)
            : null;

    [RelayCommand(CanExecute = nameof(IsRecording))]
    private void StopRecording()
    {
        // Close()/Dispose() run *inside* the lock too - see OnFrameCaptured's own comment on
        // _recordingLock for why: without this, a frame arriving on the capture thread just as this
        // (UI-thread) button handler runs could call SerWriter.WriteFrame concurrently with this
        // method's Close(), corrupting its internal frame-timestamp list.
        lock (_recordingLock)
        {
            _activeWriter?.Close();
            _activeWriter?.Dispose();
            _activeWriter = null;
        }

        IsRecording = false;
        StatusText = $"Recording stopped - {FrameCount} frame(s) written to {RecordingFilePath}.";
    }

    partial void OnSelectedCameraChanged(ICameraDevice? value) => ToggleLiveViewCommand.NotifyCanExecuteChanged();

    partial void OnIsConnectedChanged(bool value) => DisconnectCommand.NotifyCanExecuteChanged();

    partial void OnIsLiveChanged(bool value)
    {
        StartRecordingCommand.NotifyCanExecuteChanged();
        LoadTestImageCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRecordingChanged(bool value)
    {
        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        StartRecordingCommand.NotifyCanExecuteChanged();
        StopRecordingCommand.NotifyCanExecuteChanged();
        FindSunCommand.NotifyCanExecuteChanged();
        LoadTestImageCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsFindingSunChanged(bool value)
    {
        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        StartRecordingCommand.NotifyCanExecuteChanged();
        FindSunCommand.NotifyCanExecuteChanged();
        SyncMountCommand.NotifyCanExecuteChanged();
    }

    partial void OnGainChanged(double value)
    {
        if (_connectedCamera is not null && !_syncingFromDevice)
        {
            _connectedCamera.Gain = value;
        }
        PersistSettingsIfConnected();
    }

    partial void OnIsGainAutoChanged(bool value)
    {
        if (_connectedCamera is not null)
        {
            _connectedCamera.IsGainAuto = value;
        }
        PersistSettingsIfConnected();
    }

    partial void OnExposureMicrosecondsChanged(double value)
    {
        // Snaps a slider drag (or any other raw write) onto the selected range's own allowed
        // precision first - whole µs/ms for every range except the seconds one (see
        // ExposureRangeOption.DecimalPlaces) - by re-invoking the setter with the corrected value
        // and letting *that* call do the actual work below; this call has nothing left to do once
        // it's kicked off the corrected one. A tolerance well above floating-point noise but well
        // below the coarsest real step (1 whole µs) avoids re-triggering on a value that's already
        // quantized but not bit-for-bit identical to its own round-trip.
        var quantized = QuantizeToRange(value, SelectedExposureRange);
        if (Math.Abs(quantized - value) > 0.01)
        {
            ExposureMicroseconds = quantized;
            return;
        }

        if (_connectedCamera is not null && !_syncingFromDevice)
        {
            _connectedCamera.ExposureMicroseconds = value;
        }

        if (value < SelectedExposureRange.MinMicroseconds || value > SelectedExposureRange.MaxMicroseconds)
        {
            // Auto-readback, a loaded/saved value, or a spinner click at the current range's own
            // edge can all land outside the dropdown's currently-selected range - re-select
            // whichever range actually contains it rather than leaving the box/slider stuck
            // showing a value outside their own bounds.
            SelectedExposureRange = ExposureScale.FindRange(value);
        }

        // Guarded so the box's own OnExposureDisplayValueChanged doesn't try to write this same
        // value straight back into ExposureMicroseconds - it's already there.
        _syncingExposureLink = true;
        ExposureDisplayValue = value / SelectedExposureRange.UnitMicroseconds;
        _syncingExposureLink = false;

        PersistSettingsIfConnected();
    }

    /// <summary>Rounds <paramref name="microseconds"/> onto <paramref name="range"/>'s own allowed
    /// precision, expressed in that range's unit (see <see cref="ExposureRangeOption.DecimalPlaces"/>) -
    /// e.g. the nearest whole µs for "32µs ~ 10ms", the nearest whole ms for either "ms" range, or
    /// the nearest 0.001s for "1s ~ 5s". Applied to <see cref="ExposureMicroseconds"/> itself, not
    /// just how it's displayed, so a slider drag can't leave the camera set to some arbitrary
    /// fractional-µs exposure the box could never have been used to enter.</summary>
    private static double QuantizeToRange(double microseconds, ExposureRangeOption range) =>
        Math.Round(microseconds / range.UnitMicroseconds, range.DecimalPlaces, MidpointRounding.AwayFromZero) * range.UnitMicroseconds;

    /// <summary>The numeric up/down box's own edits/spinner clicks flow back into <see
    /// cref="ExposureMicroseconds"/> here, converted out of <see cref="SelectedExposureRange"/>'s
    /// unit and clamped to that range's own bounds - <see cref="OnExposureMicrosecondsChanged"/>
    /// then quantizes/reflects the result back onto this same box.</summary>
    partial void OnExposureDisplayValueChanged(double value)
    {
        if (_syncingExposureLink)
        {
            return;
        }

        ExposureMicroseconds = Math.Clamp(
            value * SelectedExposureRange.UnitMicroseconds,
            SelectedExposureRange.MinMicroseconds,
            SelectedExposureRange.MaxMicroseconds);
    }

    /// <summary>Picking a different dropdown range re-anchors both the numeric box and the slider
    /// (whose Minimum/Maximum are bound to this range) around whatever <see
    /// cref="ExposureMicroseconds"/> already is, clamped/quantized into the newly-selected range
    /// rather than jumping to some other value. The explicit <see cref="ExposureDisplayValue"/>
    /// refresh at the end is needed even when the clamp above is a no-op (already-in-range value,
    /// no cascade from <see cref="OnExposureMicrosecondsChanged"/> to do it instead) - e.g. switching
    /// ranges without the value itself needing to move still changes which unit it's shown in.</summary>
    partial void OnSelectedExposureRangeChanged(ExposureRangeOption value)
    {
        ExposureMicroseconds = Math.Clamp(ExposureMicroseconds, value.MinMicroseconds, value.MaxMicroseconds);

        _syncingExposureLink = true;
        ExposureDisplayValue = ExposureMicroseconds / value.UnitMicroseconds;
        _syncingExposureLink = false;
    }

    [RelayCommand]
    private void IncrementExposure() =>
        ExposureMicroseconds = Math.Min(ExposureMicroseconds + SelectedExposureRange.StepMicroseconds, SelectedExposureRange.MaxMicroseconds);

    [RelayCommand]
    private void DecrementExposure() =>
        ExposureMicroseconds = Math.Max(ExposureMicroseconds - SelectedExposureRange.StepMicroseconds, SelectedExposureRange.MinMicroseconds);

    partial void OnIsExposureAutoChanged(bool value)
    {
        if (_connectedCamera is not null)
        {
            _connectedCamera.IsExposureAuto = value;
        }
        PersistSettingsIfConnected();
    }

    partial void OnUsbBandwidthPercentChanged(int value)
    {
        if (_connectedCamera is not null && !_syncingFromDevice)
        {
            _connectedCamera.UsbBandwidthPercent = value;
        }
        PersistSettingsIfConnected();
    }

    partial void OnIsUsbBandwidthAutoChanged(bool value)
    {
        if (_connectedCamera is not null)
        {
            _connectedCamera.IsUsbBandwidthAuto = value;
        }
        PersistSettingsIfConnected();
    }

    partial void OnRoiWidthChanged(int value) => ScheduleApplyOutputFormatChange();

    partial void OnRoiHeightChanged(int value) => ScheduleApplyOutputFormatChange();

    /// <summary>Resets the ROI back to the full sensor - the "Full Frame" button on CaptureView.xaml.
    /// 0 means "full frame" to the device regardless of the sensor's actual size (see
    /// <see cref="RoiWidth"/>'s doc comment), so there's no need to know real dimensions here.</summary>
    [RelayCommand]
    private void ResetRoi()
    {
        RoiWidth = 0;
        RoiHeight = 0;
    }

    /// <summary>The "Reset" button on the Focus Aid panel - clears the running best-so-far low-water
    /// mark, e.g. before starting a fresh collimator adjustment pass. Also called automatically
    /// whenever live view (re)starts (see <see cref="ToggleLiveViewAsync"/>) - a "best" carried over
    /// from a previous session/camera/ROI isn't a meaningful target for a new one.</summary>
    [RelayCommand]
    private void ResetBestEdgeWidth()
    {
        _bestEdgeWidthPixels = double.PositiveInfinity;
        BestEdgeWidthText = "—";
        _recentEdgeWidthsPixels.Clear();
    }

    /// <summary>Rolling-median smoothing over the last <see cref="RecentEdgeWidthWindowSize"/> valid
    /// readings - a median rather than a mean so it resists an occasional bad frame (a burst of
    /// sensor noise, a stray reflection) the same way <see cref="FocusAnalyzer"/>'s own per-column
    /// median does, rather than letting one bad frame either flash a wrong number on screen or wrongly
    /// set a new "Best". Frames where <see cref="EdgeFocusStats.HasEdge"/> is false don't get added
    /// here at all - the window just keeps showing the last confident reading rather than being
    /// diluted by "no measurement" frames.</summary>
    private double SmoothEdgeWidth(double edgeWidthPixels)
    {
        _recentEdgeWidthsPixels.Add(edgeWidthPixels);
        if (_recentEdgeWidthsPixels.Count > RecentEdgeWidthWindowSize)
        {
            _recentEdgeWidthsPixels.RemoveAt(0);
        }

        var sorted = _recentEdgeWidthsPixels.OrderBy(v => v).ToList();
        var n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2.0;
    }

    partial void OnContrastBlackPointChanged(double value) => PersistSettingsIfConnected();

    partial void OnContrastWhitePointChanged(double value) => PersistSettingsIfConnected();

    partial void OnIsContrastAutoChanged(bool value) => PersistSettingsIfConnected();

    partial void OnDisplayBrightnessChanged(double value) => PersistSettingsIfConnected();

    // Expander open/collapsed state - app-wide UI preference, not tied to a camera model, so this
    // goes through IAppSettingsStore's own read-modify-write pattern (matching PrepareViewModel's
    // SelectedEquipmentSetup persistence) rather than ICameraSettingsStore/PersistSettingsIfConnected.
    // No debouncing needed here unlike the camera dial-in sliders - toggling an Expander is a single
    // discrete click, not something a user can rapid-fire the way a Slider drag does.
    partial void OnIsCaptureSettingsExpandedChanged(bool value) =>
        PersistAppSetting(s => s with { CaptureSettingsExpanded = value });

    partial void OnIsCameraSettingsExpandedChanged(bool value) =>
        PersistAppSetting(s => s with { CameraSettingsExpanded = value });

    partial void OnIsHistogramExpandedChanged(bool value) =>
        PersistAppSetting(s => s with { HistogramExpanded = value });

    partial void OnIsDisplaySettingsExpandedChanged(bool value) =>
        PersistAppSetting(s => s with { DisplaySettingsExpanded = value });

    partial void OnIsFocusAidExpandedChanged(bool value) =>
        PersistAppSetting(s => s with { FocusAidExpanded = value });

    partial void OnIsReticuleExpandedChanged(bool value) =>
        PersistAppSetting(s => s with { ReticuleExpanded = value });

    partial void OnIsCaptureOptionsPanelExpandedChanged(bool value)
    {
        PersistAppSetting(s => s with { CaptureOptionsPanelExpanded = value });
        OnPropertyChanged(nameof(IsCaptureDrawerOpen));
        OnPropertyChanged(nameof(IsCaptureOptionsPanelDocked));
    }

    partial void OnIsCaptureOptionsPanelPinnedChanged(bool value)
    {
        PersistAppSetting(s => s with { CaptureOptionsPanelPinned = value });
        OnPropertyChanged(nameof(IsCaptureDrawerOpen));
        OnPropertyChanged(nameof(IsCaptureOptionsPanelDocked));
    }

    /// <summary>Debounced the same way <see cref="PersistSettingsIfConnected"/> is - a GridSplitter
    /// drag raises this on every pixel of movement (see CaptureView.xaml.cs's
    /// <c>OptionsPanelHost.SizeChanged</c> handler), and writing app-settings.json that fast risks the
    /// same antivirus-scan IOException <see cref="PersistSettingsIfConnected"/>'s own doc comment
    /// describes.</summary>
    partial void OnCaptureOptionsPanelWidthChanged(double value)
    {
        _persistPanelWidthDebounceTimer ??= CreatePersistPanelWidthDebounceTimer();
        _persistPanelWidthDebounceTimer.Stop();
        _persistPanelWidthDebounceTimer.Start();
    }

    private DispatcherTimer CreatePersistPanelWidthDebounceTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            PersistAppSetting(s => s with { CaptureOptionsPanelWidth = CaptureOptionsPanelWidth });
        };
        return timer;
    }

    // Reticule toggles/angle/inset - a UI display preference with nothing to do with which camera is
    // connected (the overlay is pure on-screen geometry, see CaptureView.xaml.cs's ReticuleOverlay),
    // so these go through IAppSettingsStore the same way the Expander open/collapsed states above do,
    // rather than ICameraSettingsStore/PersistSettingsIfConnected.
    partial void OnShowCrosshairReticuleChanged(bool value) =>
        PersistAppSetting(s => s with { ShowCrosshairReticule = value });

    partial void OnShowRotationReticuleChanged(bool value) =>
        PersistAppSetting(s => s with { ShowRotationReticule = value });

    partial void OnReticuleAngleDegreesChanged(double value) =>
        PersistAppSetting(s => s with { ReticuleAngleDegrees = value });

    partial void OnReticuleInsetPixelsChanged(double value) =>
        PersistAppSetting(s => s with { ReticuleInsetPixels = value });

    partial void OnShowSpectralLineLabelsChanged(bool value) =>
        PersistAppSetting(s => s with { ShowSpectralLineLabels = value });

    partial void OnShowSpectralColorBandChanged(bool value) =>
        PersistAppSetting(s => s with { ShowSpectralColorBand = value });

    partial void OnIsSpectralOverlayExpandedChanged(bool value) =>
        PersistAppSetting(s => s with { SpectralOverlayExpanded = value });

    partial void OnSpectralOverlayFallbackPixelSizeMicronsChanged(double value) =>
        PersistAppSetting(s => s with { SpectralOverlayFallbackPixelSizeMicrons = value });

    private void PersistAppSetting(Func<AppSettings, AppSettings> update) =>
        _appSettingsStore.Save(update(_appSettingsStore.Load()));

    partial void OnSelectedColorSpaceChanged(CameraOutputFormat value) => ScheduleApplyOutputFormatChange();

    partial void OnSelectedBinningChanged(int value) => ScheduleApplyOutputFormatChange();

    /// <summary>Debounces <see cref="ApplyOutputFormatChange"/> - colour space/binning/ROI are meant
    /// to be reconfigured together in one device call, but <see cref="ResetRoi"/> (and editing both
    /// ROI textboxes back to back) sets <see cref="RoiWidth"/> and <see cref="RoiHeight"/> via two
    /// separate property assignments, each independently raising its own OnXChanged synchronously.
    /// Calling <see cref="ApplyOutputFormatChange"/> directly from both meant two overlapping calls
    /// against the same device: the first read <see cref="RoiHeight"/>'s old, not-yet-reset value
    /// (assignments inside <see cref="ResetRoi"/> happen one statement at a time, and the first
    /// async call's arguments are evaluated before it ever awaits), so which call's
    /// <c>SetOutputFormatAsync</c> actually landed last - or whether the other failed because the
    /// device was already mid-reconfiguration - decided what got applied and persisted. In practice
    /// that showed up as clicking "Full Frame" not actually restoring the full sensor: the device
    /// (and the saved <see cref="CameraSettings"/>) could end up left on the first call's
    /// half-reset width-only state. Collapsing rapid changes into one settled 150ms-later call fixes
    /// it the same way <see cref="PersistSettingsIfConnected"/>'s own debounce fixed a similar
    /// rapid-fire-write problem.
    ///
    /// Also sets <see cref="_outputFormatChangePending"/> so <see cref="OnFrameCaptured"/>'s own
    /// RoiWidth/RoiHeight-backfill-for-display logic knows not to touch those properties while a
    /// change is scheduled/in flight - debouncing alone reintroduced a second, subtler version of
    /// the same "Full Frame does nothing" bug: with live view running, a frame keeps arriving every
    /// few milliseconds throughout this whole 150ms wait (far faster than the debounce interval), and
    /// that backfill logic used to see RoiWidth/RoiHeight sitting at 0 and immediately fill them back
    /// in with the *current* (still the old, reduced) frame's own dimensions - clobbering the 0/0
    /// "full frame" request back to the previous ROI before <see cref="ApplyOutputFormatChange"/>
    /// ever got to read it.</summary>
    private void ScheduleApplyOutputFormatChange()
    {
        if (_connectedCamera is null || _syncingFromDevice)
        {
            return;
        }

        Interlocked.Exchange(ref _outputFormatChangePending, 1);
        _applyOutputFormatDebounceTimer ??= CreateApplyOutputFormatDebounceTimer();
        _applyOutputFormatDebounceTimer.Stop();
        _applyOutputFormatDebounceTimer.Start();
    }

    private DispatcherTimer CreateApplyOutputFormatDebounceTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ApplyOutputFormatChange();
        };
        return timer;
    }

    /// <summary>Colour space, binning, and ROI are all reconfigured together (see
    /// <see cref="ICameraDevice.SetOutputFormatAsync"/>) - fired from <see cref="ScheduleApplyOutputFormatChange"/>
    /// once any of the three controls' changes have settled, which is why this is a fire-and-forget
    /// async void rather than an [RelayCommand]: it's reacting to a property change, not a directly
    /// user-invoked command.</summary>
    private async void ApplyOutputFormatChange()
    {
        // Cleared on every exit path (the two early returns below included) via the outer finally -
        // see ScheduleApplyOutputFormatChange's doc comment for why OnFrameCaptured needs this held
        // for the whole duration, not just around the device call itself.
        try
        {
            if (_connectedCamera is null || _syncingFromDevice)
            {
                return;
            }

            if (IsRecording)
            {
                // Changing frame geometry/bit depth mid-file isn't representable in a SER file's
                // fixed header - revert the colour space/binning dropdowns rather than silently
                // corrupting the recording. RoiWidth/RoiHeight aren't reverted here - ICameraDevice
                // has no readback property for the currently-applied ROI to revert *to* (see its own
                // doc comment) - but CaptureView.xaml already disables the ROI controls while
                // IsRecording, so this path isn't expected to be hit via the ROI textboxes in
                // practice.
                StatusText = "Stop recording before changing colour space/binning/ROI.";
                _syncingFromDevice = true;
                SelectedColorSpace = _connectedCamera.OutputFormat;
                SelectedBinning = _connectedCamera.Binning;
                _syncingFromDevice = false;
                return;
            }

            try
            {
                await _connectedCamera.SetOutputFormatAsync(SelectedColorSpace, SelectedBinning, RoiWidth, RoiHeight);
                PersistSettingsIfConnected();
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to apply colour space/binning/ROI: {ex.Message}";
            }
        }
        finally
        {
            Interlocked.Exchange(ref _outputFormatChangePending, 0);
        }
    }

    /// <summary>Saves the current dial-in state for <see cref="_connectedCamera"/>'s model (see
    /// <see cref="ICameraSettingsStore"/>) - a no-op while nothing's connected, or while a
    /// property is being set *from* the device rather than by the user (loading previously-saved
    /// settings back in on connect, or live auto-readback while an Auto flag is on - neither of
    /// those should immediately re-save what was just read).
    ///
    /// Debounced rather than written synchronously on every call: a Slider raises its bound
    /// property's setter on every pixel of drag movement, and dragging one quickly can fire this
    /// dozens of times a second - writing camera-settings.json that fast was seen in practice to
    /// throw an IOException ("The requested operation cannot be performed on a file with a
    /// user-mapped section open") when a rapid preceding write was still being scanned/indexed by
    /// something else (e.g. antivirus) at the moment the next one tried to open the same file.
    /// Only the value still current 300ms after the last change actually gets saved.</summary>
    private void PersistSettingsIfConnected()
    {
        if (_connectedCamera is null || _syncingFromDevice)
        {
            return;
        }

        _persistSettingsDebounceTimer ??= CreatePersistSettingsDebounceTimer();
        _persistSettingsDebounceTimer.Stop();
        _persistSettingsDebounceTimer.Start();
    }

    private DispatcherTimer CreatePersistSettingsDebounceTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_connectedCamera is not null)
            {
                _cameraSettingsStore.Save(_connectedCamera.Name, BuildCurrentCameraSettings());
            }
        };
        return timer;
    }

    /// <summary>Immediately performs any save <see cref="PersistSettingsIfConnected"/>'s debounce
    /// timer is still waiting to run, so a change made just before disconnecting isn't lost. Called
    /// from <see cref="DisconnectCoreAsync"/> while <see cref="_connectedCamera"/> is still set.</summary>
    private void FlushPendingCameraSettings()
    {
        if (_persistSettingsDebounceTimer is not { IsEnabled: true } timer || _connectedCamera is null)
        {
            return;
        }

        timer.Stop();
        _cameraSettingsStore.Save(_connectedCamera.Name, BuildCurrentCameraSettings());
    }

    /// <summary>Builds a <see cref="CameraSettings"/> snapshot from the view model's current dial-in
    /// properties - shared by <see cref="PersistSettingsIfConnected"/> (the per-camera-*model* saved
    /// preferences) and <see cref="WriteCaptureMetadata"/> (the per-*recording* snapshot in
    /// <see cref="CaptureMetadata.CameraSettingsUsed"/>), so the two field lists can't drift apart.
    /// Callers are responsible for only calling this while <see cref="_connectedCamera"/> is set.</summary>
    private CameraSettings BuildCurrentCameraSettings() => new(
        Gain,
        ExposureMicroseconds,
        UsbBandwidthPercent,
        IsGainAuto,
        IsExposureAuto,
        IsUsbBandwidthAuto,
        SelectedColorSpace,
        SelectedBinning,
        ContrastBlackPoint,
        ContrastWhitePoint,
        IsContrastAuto,
        RoiWidth,
        RoiHeight,
        DisplayBrightness);

    private void OnFrameCaptured(object? sender, CameraFrame frame)
    {
        // The camera's actual capture rate - every frame, unlike the throttled preview redraw
        // further down - reported on the shared status bar roughly once a second (see RASTA's own
        // StatusBar for the pattern this follows).
        _frameArrivalCount++;
        var nowForFrameRate = DateTime.UtcNow;
        var sinceLastFrameRateUpdate = nowForFrameRate - _lastFrameRateUpdateUtc;
        if (sinceLastFrameRateUpdate >= FrameRateUpdateInterval)
        {
            var fps = _frameArrivalCount / sinceLastFrameRateUpdate.TotalSeconds;
            _frameArrivalCount = 0;
            _lastFrameRateUpdateUtc = nowForFrameRate;
            _dispatcher.BeginInvoke(() => _statusBar.CaptureFrameRateText = $"Capture: {fps:0.0} fps");
        }

        // UI-display convenience only: ROI is now a real hardware setting (see
        // ApplyOutputFormatChange), not a post-capture crop, so a RoiWidth/RoiHeight of 0 already
        // means "full frame" to the device - this just fills the textboxes in with the actual sensor
        // dimensions the first time they're seen, rather than leaving them showing a bare,
        // unhelpful "0". _syncingFromDevice guards it from triggering another (unnecessary,
        // stream-restarting) ApplyOutputFormatChange call - 0 and the real number already mean the
        // same thing to the device, so there's nothing to actually reapply.
        //
        // _outputFormatChangePending additionally skips this entirely while a change is
        // scheduled/in flight (see ScheduleApplyOutputFormatChange's own doc comment) - without it,
        // clicking "Full Frame" while live could see this backfill run on the very next frame
        // (arriving well within the 150ms debounce window, let alone the device's own reconfigure
        // time) and write the *current, still-reduced* frame's dimensions straight back into
        // RoiWidth/RoiHeight, so ApplyOutputFormatChange ended up re-requesting the same ROI it
        // already had instead of the full sensor.
        if (Interlocked.CompareExchange(ref _outputFormatChangePending, 0, 0) == 0 && (RoiWidth <= 0 || RoiHeight <= 0))
        {
            var fullWidth = frame.Width;
            var fullHeight = frame.Height;
            _dispatcher.BeginInvoke(() =>
            {
                _syncingFromDevice = true;
                if (RoiWidth <= 0)
                {
                    RoiWidth = fullWidth;
                }
                if (RoiHeight <= 0)
                {
                    RoiHeight = fullHeight;
                }
                _syncingFromDevice = false;
            });
        }

        // WriteFrame itself now runs *inside* the lock, not just the reference grab - StopRecording
        // takes the same lock around Close()/Dispose() (see below), so a WriteFrame call on this
        // (capture) thread and a Close() call on the UI thread (Stop Recording button) can never run
        // concurrently against the same SerWriter instance. They used to be able to: SerWriter has no
        // internal synchronization of its own (its _frameTimestampsUtcTicks list is plain, mutated by
        // WriteFrame and enumerated by Close()), and the previous, narrower lock only protected
        // grabbing the writer reference - real-hardware testing hit exactly this race as
        // "InvalidOperationException: Collection was modified; enumeration operation may not execute"
        // out of Close(), from a frame arriving concurrently with the Stop Recording button.
        var writerWasOpen = false;
        lock (_recordingLock)
        {
            var writer = _activeWriter;
            if (writer is not null)
            {
                if (_writerNeedsOpening)
                {
                    writer.Open(RecordingFilePath!, frame.Width, frame.Height, frame.BitDepth);
                    _writerNeedsOpening = false;
                }

                writer.WriteFrame(frame);
                writerWasOpen = true;
            }
        }

        if (writerWasOpen)
        {
            _dispatcher.BeginInvoke(() => FrameCount++);
        }

        var now = DateTime.UtcNow;
        if (now - _lastPreviewRedrawUtc < PreviewRedrawInterval)
        {
            return;
        }

        // Scanning a full-resolution frame (histogram + contrast stretch, potentially several
        // million pixels on the ASI678MM's 3840x2160 sensor) is real work - too slow to do
        // synchronously right here. This method runs *on the camera's own capture thread* (the
        // same one calling ASIGetVideoData in a loop, or handling Altair's native callback), so
        // blocking it on that work is exactly what was starving the SDK's ring buffer and showing
        // up as both dropped frames and a laggy-feeling live view: the actual camera capture rate
        // was being throttled by our own preview drawing, not by the camera or USB link. Offloading
        // to the thread pool - guarded so at most one preview frame is ever being processed at once,
        // silently dropping this one for *preview* purposes if the last one hasn't finished yet -
        // decouples the two, so a slow preview redraw can never hold up frame acquisition or
        // recording (both of which already happened above, before this throttle check).
        if (Interlocked.CompareExchange(ref _previewProcessingInFlight, 1, 0) != 0)
        {
            return;
        }
        _lastPreviewRedrawUtc = now;

        var camera = sender as ICameraDevice;
        Task.Run(() => ProcessPreviewFrame(frame, camera));
    }

    private void ProcessPreviewFrame(CameraFrame frame, ICameraDevice? camera)
    {
        try
        {
            // frame is already exactly the ROI - see ApplyOutputFormatChange - so the histogram,
            // auto-stretch, and preview bitmap all just operate on it directly, no separate crop step.
            var stats = FramePreview.ComputeHistogramStats(frame);

            // Auto-stretch is computed fresh from *this* frame's histogram rather than read back
            // off the (UI-thread-owned) ContrastBlackPoint/WhitePoint properties, so the preview
            // reacts immediately rather than trailing a frame behind - see
            // FramePreview.ComputeAutoStretch's doc comment for why this exists at all (SharpCap
            // parity, not just cosmetic).
            var isContrastAuto = IsContrastAuto;
            var (blackPoint, whitePoint) = isContrastAuto
                ? FramePreview.ComputeAutoStretch(stats.Histogram)
                : (ContrastBlackPoint, ContrastWhitePoint);

            // A *fixed* zoom percentage is the user deliberately asking to inspect real detail -
            // checking focus on the spectral line itself is the actual point of this app - so the
            // preview needs genuinely native-resolution pixels there, not FramePreview's default
            // downsample-for-performance cap (960px longest side): zooming into an already-
            // downsampled bitmap would just show a magnified, blurry copy of detail that's already
            // been thrown away, defeating the purpose. Passing the frame's own full size as
            // maxDimension makes ComputeDownsampleGrid's scale factor exactly 1 (no downsampling) -
            // see its own doc comment. Auto/FitWidth/FitHeight keep the cheap default: their whole
            // point is fitting the available viewport, not pixel-peeping, so there's nothing to gain
            // from the extra work. This only runs on the already-offloaded preview thread (see the
            // throttling comment above), never the capture thread, so it can't affect capture fps -
            // the cost of a deliberate zoom-in is a slower-feeling *preview* redraw only.
            var stretchMaxDimension = SelectedZoomOption.Kind == ZoomKind.Fixed
                ? Math.Max(frame.Width, frame.Height)
                : FramePreview.DefaultMaxPreviewDimension;
            var (stretchedPixels, previewWidth, previewHeight) = FramePreview.Stretch(frame, blackPoint, whitePoint, stretchMaxDimension, DisplayBrightness);
            var droppedFrames = camera?.DroppedFrameCount ?? 0;

            // Collimator-focus aid (see FocusAnalyzer's own doc comment) - operates on frame at its
            // own native resolution, not a downsampled copy: sub-pixel edge-width measurement needs
            // full column resolution, and downsampling the width would blur exactly the edge
            // transition this is trying to measure.
            var focusStats = FocusAnalyzer.MeasureEdgeSteepness(frame);

            // Live spectral-line overlay (labels + colour gradient band - see SolScan.Processing.
            // Spectrum.SpectralOverlayAnalyzer) - gated on its own slower cadence than the preview
            // redraw itself (SpectralOverlayUpdateInterval's own doc comment explains why), and only
            // attempted once a live instrument is actually resolved. Pixel size prefers the connected
            // camera's own real value, falling back to SpectralOverlayFallbackPixelSizeMicrons when
            // that's unknown - the normal case for a loaded test image (see LoadTestImage), which
            // isn't tied to any real camera at all. Reading/writing _lastSpectralOverlayUtc here (a
            // plain field, not Interlocked) is safe because _previewProcessingInFlight already
            // guarantees at most one ProcessPreviewFrame call is ever running at a time.
            List<SpectralLineLabel>? spectralLabels = null;
            GradientStopCollection? spectralGradientStops = null;
            string? spectralOverlayDiagnostics = null;
            var overlayNow = DateTime.UtcNow;
            if ((ShowSpectralLineLabels || ShowSpectralColorBand)
                && _connectedInstrument is { } instrument
                && overlayNow - _lastSpectralOverlayUtc >= SpectralOverlayUpdateInterval)
            {
                var pixelSizeMicrons = _connectedCameraProfile?.PixelSizeMicrons ?? SpectralOverlayFallbackPixelSizeMicrons;
                _lastSpectralOverlayUtc = overlayNow;
                try
                {
                    var maxShiftPixels = Math.Max(1, frame.Height / 2) - 1;
                    var overlay = SpectralOverlayAnalyzer.Analyze(frame, instrument, pixelSizeMicrons, SelectedBinning, maxShiftPixels);
                    var downsampleScale = FramePreview.ComputeDownsampleScale(frame.Width, frame.Height, stretchMaxDimension);

                    // Diagnostic-only (shown in the "Spectral Overlay" Expander) - real numbers to check
                    // against rather than guessing from what's on screen, same reasoning as
                    // SolScan.Tools annotate's own console output.
                    var best = overlay.Identification.AllCandidates.Count > 0 ? overlay.Identification.AllCandidates[0] : null;
                    spectralOverlayDiagnostics = best is null
                        ? "No candidates scored."
                        : $"Best: {best.Ray.Label} (score {best.Score:F3}, {(overlay.Identification.IdentifiedRay is not null ? "confident" : "not confident")}) | "
                            + $"centreRow={overlay.CentreRowInFrame:F1}/{frame.Height} | "
                            + $"Å/px={overlay.AnchorDispersionAngstromsPerPixel:F4} | "
                            + $"maxShift={maxShiftPixels}px | visibleLines={overlay.VisibleLines.Count} | "
                            + $"downsampleScale={downsampleScale}";

                    if (ShowSpectralLineLabels)
                    {
                        spectralLabels = BuildSpectralLineLabels(overlay, downsampleScale);
                    }
                    if (ShowSpectralColorBand)
                    {
                        spectralGradientStops = BuildSpectralGradientStops(overlay, maxShiftPixels);
                    }
                }
                catch (Exception ex)
                {
                    // Best-effort - a failed overlay pass (e.g. a genuinely blank/degenerate frame)
                    // shouldn't break the live preview itself; this throttled tick's overlay is just
                    // skipped, the previous one stays on screen until the next successful pass. Still
                    // surfaced in the diagnostics text, though - a silently-skipped pass otherwise looks
                    // identical to "nothing changed" from the UI, hiding a real failure.
                    spectralOverlayDiagnostics = $"Overlay pass failed: {ex.Message}";
                }
            }

            // While Auto is on, the camera's own algorithm - not the user - is driving that value,
            // so read it back here (cheap; already off the capture thread) and reflect it on the
            // slider, guarded so OnXChanged doesn't immediately push it right back.
            var gainIfAuto = camera is { IsGainAuto: true } ? camera.Gain : (double?)null;
            var exposureIfAuto = camera is { IsExposureAuto: true } ? camera.ExposureMicroseconds : (double?)null;
            var bandwidthIfAuto = camera is { IsUsbBandwidthAuto: true } ? camera.UsbBandwidthPercent : (int?)null;

            _dispatcher.BeginInvoke(() =>
            {
                HistogramGeometry = BuildHistogramGeometry(stats.Histogram);
                HistogramStatsText = $"{stats.BitDepth}-bit  Min:{stats.MinValue}  Max:{stats.MaxValue}  Avg:{stats.AverageValue:0}";
                DroppedFrameCount = droppedFrames;
                _lastFrameAverageBrightness = stats.AverageValue; // see FindSunAsync's fine-tune hill-climb
                if (focusStats.HasEdge)
                {
                    var smoothedEdgeWidth = SmoothEdgeWidth(focusStats.EdgeWidthPixels);
                    EdgeWidthText = $"{smoothedEdgeWidth:0.00} px";
                    if (smoothedEdgeWidth < _bestEdgeWidthPixels)
                    {
                        _bestEdgeWidthPixels = smoothedEdgeWidth;
                        BestEdgeWidthText = $"{_bestEdgeWidthPixels:0.00} px";
                    }
                }
                else
                {
                    EdgeWidthText = "No edge detected";
                }
                RenderPreview(previewWidth, previewHeight, stretchedPixels);

                // Only overwritten on a throttled tick that actually ran the analysis (see above) -
                // otherwise the previous overlay stays on screen rather than flickering empty between
                // updates.
                if (spectralLabels is not null)
                {
                    SpectralLineLabels = new ObservableCollection<SpectralLineLabel>(spectralLabels);
                }
                if (spectralGradientStops is not null)
                {
                    SpectralGradientStops = spectralGradientStops;
                }
                if (spectralOverlayDiagnostics is not null)
                {
                    SpectralOverlayDiagnosticsText = spectralOverlayDiagnostics;
                }

                if (isContrastAuto)
                {
                    // Reflects what was actually just applied above onto the (disabled-while-auto)
                    // sliders - purely cosmetic, doesn't feed back into anything.
                    ContrastBlackPoint = blackPoint;
                    ContrastWhitePoint = whitePoint;
                }

                if (gainIfAuto is null && exposureIfAuto is null && bandwidthIfAuto is null)
                {
                    return;
                }

                _syncingFromDevice = true;
                if (gainIfAuto is { } g)
                {
                    Gain = g;
                }
                if (exposureIfAuto is { } e)
                {
                    ExposureMicroseconds = e;
                }
                if (bandwidthIfAuto is { } b)
                {
                    UsbBandwidthPercent = b;
                }
                _syncingFromDevice = false;
            });
        }
        finally
        {
            Interlocked.Exchange(ref _previewProcessingInFlight, 0);
        }
    }

    /// <summary>Converts <see cref="SpectralOverlayAnalyzer"/>'s raw-frame-space
    /// <c>VisibleSpectralLine.RowInFrame</c> into preview-bitmap pixel space (the same downsampling
    /// <see cref="FramePreview.Stretch"/> already applied to the frame being rendered this tick) -
    /// CaptureView.xaml.cs's own <c>MapPreviewBitmapYToControlY</c> does the one remaining step
    /// (bitmap space → actual on-screen control space, via the live zoom scale). <see cref="SpectralLineLabel.IsConfident"/>
    /// is true only for <see cref="SpectralLineIdentificationResult.IdentifiedRay"/> itself - every
    /// other visible line is the layout's own best-guess projection, not a separately confirmed match.</summary>
    private static List<SpectralLineLabel> BuildSpectralLineLabels(SpectralOverlayResult overlay, int downsampleScale)
    {
        var labels = new List<SpectralLineLabel>(overlay.VisibleLines.Count);
        foreach (var line in overlay.VisibleLines)
        {
            var isConfident = overlay.Identification.IdentifiedRay == line.Ray;
            labels.Add(new SpectralLineLabel(line.Ray, line.RowInFrame / downsampleScale, isConfident));
        }

        return labels;
    }

    /// <summary>Samples <see cref="SpectralColor.ToRgb"/> across the frame's own full sampled
    /// pixel-shift range (top = most negative shift, bottom = most positive - matching a
    /// <c>LinearGradientBrush</c> with <c>StartPoint="0,0" EndPoint="0,1"</c>) to build the colour
    /// band's gradient stops. <see cref="GradientStop"/>/<see cref="GradientStopCollection"/> are WPF
    /// <c>Freezable</c>s - normally thread-affine to whichever thread creates them, so this
    /// deliberately <see cref="Freezable.Freeze"/>s the result before returning it from this
    /// background-thread call, making it safe for the UI thread (which didn't create it) to bind to.
    /// Null when there's nothing to anchor a wavelength range to (see
    /// <see cref="SpectralOverlayResult.AnchorDispersionAngstromsPerPixel"/>'s own doc comment).</summary>
    private const int SpectralGradientStopCount = 24;

    private static GradientStopCollection? BuildSpectralGradientStops(SpectralOverlayResult overlay, int maxShiftPixels)
    {
        if (overlay.AnchorDispersionAngstromsPerPixel is not { } dispersion || overlay.Identification.AllCandidates.Count == 0)
        {
            return null;
        }

        var anchorWavelength = overlay.Identification.AllCandidates[0].Ray.WavelengthAngstroms;
        var stops = new GradientStopCollection();
        for (var i = 0; i < SpectralGradientStopCount; i++)
        {
            var offset = i / (double)(SpectralGradientStopCount - 1);
            var shift = ((2 * offset) - 1) * maxShiftPixels; // offset 0 -> -maxShiftPixels, 1 -> +maxShiftPixels
            var wavelength = anchorWavelength + (shift * dispersion);
            var (r, g, b) = SpectralColor.ToRgb(wavelength);
            stops.Add(new GradientStop(Color.FromRgb(r, g, b), offset));
        }

        stops.Freeze();
        return stops;
    }

    private void RenderPreview(int width, int height, byte[] pixels)
    {
        if (PreviewBitmap is null || PreviewBitmap.PixelWidth != width || PreviewBitmap.PixelHeight != height)
        {
            PreviewBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Gray8, palette: null);
        }

        PreviewBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width, offset: 0);
    }

    /// <summary>Height of the plotted bar-chart coordinate space, in <see cref="BuildHistogramGeometry"/>'s
    /// own local units - CaptureView.xaml's histogram Canvas must be sized to match
    /// (<see cref="HistogramPlotWidth"/> x this).</summary>
    public const double HistogramPlotHeight = 100;

    /// <summary>Width of the plotted bar-chart coordinate space: <see cref="FramePreview.HistogramBucketCount"/>
    /// buckets plus a small margin on each side (see <see cref="BuildHistogramGeometry"/>'s doc
    /// comment) - CaptureView.xaml's histogram Canvas must be sized to match.</summary>
    public const double HistogramPlotWidth = FramePreview.HistogramBucketCount + (2 * HistogramEdgeMargin);

    private const double HistogramEdgeMargin = 2;

    /// <summary>
    /// Turns raw bucket counts into a filled step/bar-chart outline in a fixed
    /// [<see cref="HistogramPlotWidth"/>]x[<see cref="HistogramPlotHeight"/>] space - CaptureView.xaml
    /// sizes its histogram Canvas to exactly that, rather than letting the Viewbox size itself off
    /// this geometry's own (data-dependent) bounds, for the same edge-visibility reason as the
    /// margin below.
    ///
    /// Bar heights come from <see cref="FramePreview.ComputeHistogramBarHeights"/> - a *log* scale
    /// against the tallest bucket, not linear - see its doc comment for why: on this panel's compact
    /// 60px height, a linear scale left anything but the single dominant (usually background) bucket
    /// rendering under a pixel tall, so a heavily overexposed frame's real, growing "clipping" hump
    /// would appear to flatten to nothing instead of becoming visible.
    ///
    /// Deliberately a *stepped* outline (each bucket gets a full-width flat-topped rectangle) and
    /// not a line connecting bucket-centre points: a heavily overexposed frame can have virtually
    /// every pixel land in one bucket (typically the last one), and a centre-point line would draw
    /// that as a triangle whose peak is a single, literally zero-width point - which, especially
    /// sitting right on the plot's own edge, can render as invisible (no fillable area) rather than
    /// a small sliver. A stepped bar always has real width, however extreme the spike - but a fully
    /// saturated frame (every sampled pixel identical, all piled into the very last bucket) still
    /// rendered as a completely blank graph in practice, confirmed on real ASI678MM hardware: with
    /// zero margin, that single bar sits exactly flush against the plotted area's own right edge,
    /// where WPF's layout rounding can round its (already sub-pixel-thin) fill area away to nothing.
    /// <see cref="HistogramEdgeMargin"/> keeps every bar - including one at bucket 0 or the very
    /// last bucket - comfortably inside the plotted area's own bounds instead of flush against them.
    /// </summary>
    private static Geometry BuildHistogramGeometry(int[] rawHistogram)
    {
        var barHeights = FramePreview.ComputeHistogramBarHeights(rawHistogram);

        var figure = new PathFigure { StartPoint = new Point(HistogramEdgeMargin, HistogramPlotHeight), IsClosed = true };
        for (var i = 0; i < rawHistogram.Length; i++)
        {
            var barTop = HistogramPlotHeight - (barHeights[i] * HistogramPlotHeight);
            figure.Segments.Add(new LineSegment(new Point(HistogramEdgeMargin + i, barTop), isStroked: true)); // rise/fall to this bucket's height
            figure.Segments.Add(new LineSegment(new Point(HistogramEdgeMargin + i + 1, barTop), isStroked: true)); // flat top across the full bucket width
        }
        figure.Segments.Add(new LineSegment(new Point(HistogramEdgeMargin + rawHistogram.Length, HistogramPlotHeight), isStroked: true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }
}
