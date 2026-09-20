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
using QuadraticPolynomial = SolScan.Processing.Math.QuadraticPolynomial;

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

    // Spectral analysis (the live line overlay + the camera-focus aid - see TryStartSpectralAnalysis):
    // one shared cadence and one shared curvature fit per pass, deliberately slower than the preview
    // redraw - a person turning a grating or focus ring by hand doesn't need 20fps from these, and each
    // pass is real work at the ASI678MM's 3840x2160. Read/written only from the preview-processing
    // thread (single-flight via _previewProcessingInFlight), or reset from the UI thread by
    // LoadTestImage before it starts that thread. NOT YET VALIDATED against real hardware - a starting value.
    private static readonly TimeSpan SpectralAnalysisInterval = TimeSpan.FromMilliseconds(300);
    private DateTime _lastSpectralAnalysisUtc = DateTime.MinValue;

    /// <summary>1 while a spectral-analysis pass is running on its own worker. Deliberately separate
    /// from <see cref="_previewProcessingInFlight"/>: sharing that flag was what let a slow analysis
    /// stall the preview itself (see <see cref="TryStartSpectralAnalysis"/>).</summary>
    private int _spectralAnalysisInFlight;

    /// <summary>The most recently *confident* live spectral-line identification (see
    /// <see cref="SpectralOverlayAnalyzer"/>/<see cref="SpectralLineIdentifier"/>) - null until the
    /// overlay first confidently locks onto a line, and reset whenever live view (re)starts (see
    /// <see cref="ToggleLiveViewAsync"/>), same "not meaningful carried over from a previous session"
    /// reasoning as <see cref="_bestEdgeWidthPixels"/>. Read by <see cref="WriteCaptureMetadata"/> to
    /// populate <see cref="CaptureMetadata.StudiedRay"/> - the point of this field existing at all is
    /// closing the "no metadata for the scan" gap at its source (a full-frame, tightly-cropped
    /// recording has nowhere near enough spectral context to identify its own line after the fact -
    /// see this session's own investigation), rather than trying to recover it from an already-cropped
    /// file later. Written only from the background preview-processing thread (<see cref="ProcessPreviewFrame"/>),
    /// written only by the single-flight spectral-analysis worker (<see cref="_spectralAnalysisInFlight"/>) -
    /// a plain reference write is safe here.
    /// Deliberately never cleared just because a later tick turns unconfident - it's "the last line
    /// the overlay was sure about", which stays a reasonable answer through a few noisy/ambiguous
    /// frames. Can go stale if the user changes lines and starts recording before a fresh confident
    /// read arrives - a known, accepted gap (a slightly-stale line is still more useful than always
    /// writing null).</summary>
    private SpectralRay? _lastConfidentSpectralRay;

    // "Find Sun" fine-tune (see FindSunAsync) - a simple brightness hill-climb, not the full
    // spiral-search-then-hill-climb algorithm CLAUDE.md's "Visual fine-centering" describes: the
    // ephemeris slew should already land the Sun somewhere in frame, so there's no need for a
    // from-zero-signal search phase for v1.
    //
    // UNTESTED against real hardware - the constants below are unvalidated first guesses (see
    // FindSunAsync's own doc comment) and may well need retuning once tried against a real mount +
    // camera pointed at the actual Sun.
    //
    // Steps are sized as angles (a pulse's duration is derived from the step and the nudge rate), not
    // as a fixed pulse at a fraction of max rate: the first version's ~38" nudges were far too small
    // to see or to measure against the Sun's ~1900" disk.
    private const double FindSunNudgeRateFraction = 0.05; // of the mount's own max slew rate
    private const double FindSunCoarseStepDeg = 0.1; // ~1/5 of the Sun's diameter
    private const double FindSunFineStepDeg = 0.025;
    private static readonly TimeSpan FindSunMaxPulseDuration = TimeSpan.FromSeconds(5);
    private const int FindSunMaxStepsPerAxis = 10;
    private const int FindSunFramesPerMeasurement = 5;
    private const int FindSunNoiseSigmas = 3; // an "improvement" must beat this many standard errors
    // Floor on the improvement threshold, relative to the brightness - guards against a near-zero
    // measured noise (e.g. a saturated or perfectly static frame) making any wobble look real.
    // Relative, not absolute: Mono16's raw average sits ~256x higher than Mono8's for the same scene.
    private const double FindSunMinRelativeImprovement = 0.002;
    // A frame whose average is below this fraction of full scale is treated as dark - the slit isn't
    // seeing the Sun, so a brightness hill-climb has nothing to climb (its readings are just sensor
    // noise). From the first real run: average 46 of 65535 (0.07%), max 1.8%, with the Sun not in the slit.
    private const double FindSunDarkFractionOfFullScale = 0.005;
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

    /// <summary>Companions to <see cref="_lastFrameAverageBrightness"/> for the Find Sun fine-tune,
    /// set at the same place (UI thread): a running count of processed preview frames (so a
    /// measurement can wait for genuinely fresh frames rather than re-reading one), plus the frame's
    /// max value/bit depth (so saturation - a flat, uninformative signal - can be spotted and logged).</summary>
    private long _previewFrameCounter;
    private double _lastFrameMaxValue;
    private int _lastFrameBitDepth;

    /// <summary>Fraction of the last frame's sampled pixels at full scale (top histogram bucket) - a
    /// few hot pixels are far below the limit, a saturated Sun/sky isn't.</summary>
    private double _lastFrameSaturatedFraction;

    /// <summary>Brightness centroid of the last frame (0-1 of width/height; NaN when not computed or no
    /// signal). Only computed while <see cref="_findSunMeasuringCentroid"/> is set, since it costs a
    /// strided pass over the frame the preview otherwise doesn't need.</summary>
    private double _lastFrameCentreX = double.NaN;
    private double _lastFrameCentreY = double.NaN;
    private volatile bool _findSunMeasuringCentroid;

    /// <summary>The current Find Sun run's diagnostic log; null outside a run, or if it couldn't be opened.</summary>
    private ProcessingLog? _findSunLog;

    /// <summary>The currently-open Hand Control window, if any - tracked so a second click on
    /// "Hand Control…" brings the existing one to front instead of opening a duplicate (two windows
    /// independently sending MoveAxis would race each other).</summary>
    private HandControlWindow? _handControlWindow;

    /// <summary>The pop-out focus-graph window (see <see cref="OpenFocusGraph"/>), if open. While it is,
    /// both focus aids run regardless of the Focus Aid Expander (the graphs need their data) and build
    /// their plot data. <see cref="_isFocusGraphOpen"/> mirrors it as a volatile flag because the preview
    /// and analysis threads read it.</summary>
    private FocusGraphWindow? _focusGraphWindow;
    private volatile bool _isFocusGraphOpen;

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

    /// <summary>Raw gain - whole numbers only (see <see cref="OnGainChanged"/>), within
    /// <see cref="MinGain"/>-<see cref="MaxGain"/> (see <see cref="ICameraDevice.Gain"/>).</summary>
    [ObservableProperty]
    private double gain = 150;

    /// <summary>The connected camera's own actual Gain range (see <see cref="ICameraDevice.MinGain"/>/
    /// <see cref="ICameraDevice.MaxGain"/>) - defaults to the ASI678MM's own 0-600 range before a
    /// camera connects, same as <see cref="Gain"/>'s own default matches that range. Updated once,
    /// right after connecting (see <see cref="ToggleLiveViewAsync"/>) - never changes for the life
    /// of a connection.</summary>
    [ObservableProperty]
    private double minGain;

    [ObservableProperty]
    private double maxGain = 600;

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

    /// <summary>Camera-focus aid readout - see <see cref="SpectralLineFocusAnalyzer"/>. The full width
    /// at half depth of the studied spectral line, in pixels: the number to *minimize* while adjusting
    /// the camera's own focus, distinct from <see cref="EdgeWidthText"/>, which tracks the collimator.</summary>
    [ObservableProperty]
    private string lineWidthText = "—";

    /// <summary>Running low-water mark of <see cref="LineWidthText"/>'s underlying value - the camera-
    /// focus counterpart to <see cref="BestEdgeWidthText"/>, same reasoning.</summary>
    [ObservableProperty]
    private string bestLineWidthText = "—";

    /// <summary>How deep the measured line's dip is, as a percentage of its local continuum - context
    /// for <see cref="LineWidthText"/>, since a very shallow dip makes the width less trustworthy.</summary>
    [ObservableProperty]
    private string lineDepthText = "—";

    /// <summary>See <see cref="_bestEdgeWidthPixels"/> - same UI-thread-only threading rationale.</summary>
    private double _bestLineWidthPixels = double.PositiveInfinity;

    /// <summary>Rolling-median window for the line-width reading. Smaller than
    /// <see cref="RecentEdgeWidthWindowSize"/> because this aid updates less often (see
    /// <see cref="LineFocusUpdateInterval"/>), so five readings would lag a real focus change by over a second.</summary>
    private const int RecentLineWidthWindowSize = 3;

    private readonly List<double> _recentLineWidthsPixels = new(RecentLineWidthWindowSize);

    /// <summary>Latest camera-focus (line profile) and collimator-focus (edge profile) graph data for
    /// <see cref="FocusGraphWindow"/> - only populated while that window is open.</summary>
    [ObservableProperty]
    private ProfilePlotData? lineProfilePlot;

    [ObservableProperty]
    private ProfilePlotData? edgeProfilePlot;

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

    /// <summary>Opens the pop-out, modeless focus-graph window (see Views/FocusGraphWindow.xaml) - a live
    /// plot of what each focus aid is measuring, in the spirit of sunscan-app's Spectrum chart. Brings the
    /// existing one to front rather than opening a second. While open, both aids run and feed it
    /// regardless of the Focus Aid Expander (see <see cref="_isFocusGraphOpen"/>).</summary>
    [RelayCommand]
    private void OpenFocusGraph()
    {
        if (_focusGraphWindow is not null)
        {
            _focusGraphWindow.Activate();
            return;
        }

        // The analysis worker is throttled by _lastSpectralAnalysisUtc - clear it so the graph doesn't
        // sit empty for a cadence tick after opening.
        _lastSpectralAnalysisUtc = DateTime.MinValue;

        _focusGraphWindow = new FocusGraphWindow { DataContext = this };
        _isFocusGraphOpen = true;
        _focusGraphWindow.Closed += (_, _) =>
        {
            _focusGraphWindow = null;
            _isFocusGraphOpen = false;
            LineProfilePlot = null;
            EdgeProfilePlot = null;
        };
        _focusGraphWindow.Show();
    }

    private static readonly Color PlotAccent = Color.FromRgb(0xFF, 0xB3, 0x47);
    private static readonly Color PlotReference = Color.FromRgb(0x88, 0x88, 0x88);

    /// <summary>The edge profile plus, per measured edge, its 10%/90% crossing points and the two plateau
    /// levels it was measured between - what the "Edge width" number was derived from.</summary>
    private static ProfilePlotData BuildEdgePlot(EdgeFocusDetail detail)
    {
        var lines = new List<PlotLine>();
        foreach (var edge in detail.Edges)
        {
            lines.Add(PlotLine.Horizontal(edge.LowLevel, PlotReference));
            lines.Add(PlotLine.Horizontal(edge.HighLevel, PlotReference));
            lines.Add(PlotLine.Vertical(edge.LeftCrossing, PlotAccent));
            lines.Add(PlotLine.Vertical(edge.RightCrossing, PlotAccent));
        }

        return new ProfilePlotData(detail.Profile, 0, lines);
    }

    /// <summary>The straightened line profile plus its continuum level and the half-depth bar between the
    /// two crossings - what the FWHM number was derived from. With no confident line, whatever reference
    /// levels could still be worked out are drawn dashed, to show why it was rejected.</summary>
    private static ProfilePlotData BuildLinePlot(SpectralLineFocusDetail detail)
    {
        var lines = new List<PlotLine>();
        if (detail.Continuum is { } continuum)
        {
            lines.Add(PlotLine.Horizontal(continuum, PlotReference));
        }

        if (detail is { HalfLevel: { } half, LeftCrossingShift: { } left, RightCrossingShift: { } right })
        {
            lines.Add(PlotLine.Segment(left, right, half, PlotAccent));
            lines.Add(PlotLine.Vertical(left, PlotAccent));
            lines.Add(PlotLine.Vertical(right, PlotAccent));
        }
        else if (detail.HalfLevel is { } unconfirmedHalf)
        {
            lines.Add(PlotLine.Horizontal(unconfirmedHalf, PlotAccent));
        }

        return new ProfilePlotData(detail.Profile.Values, detail.Profile.MinShiftPixels, lines);
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
    /// locate and centre the Sun using the live view (<see cref="RunFineTuneAsync"/>): a spiral search
    /// if the frame is dark, then exposure/gain adjustment, then centring. Finally offers to sync the
    /// mount's pointing model to the result via Alpaca (only if the Sun was actually found).
    /// Cancellable via <see cref="CancelFindSunCommand"/>; every run writes a diagnostic log.
    ///
    /// NOT YET VERIFIED against real hardware: the search phase and the centroid-based centring are new
    /// (the brightness hill-climb has been run on the mount and Sun - see CLAUDE.md), and their step
    /// sizes/thresholds are first guesses to be tuned from the logs.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFindSun))]
    private async Task FindSunAsync()
    {
        using var cts = new CancellationTokenSource();
        _findSunCts = cts;
        var ct = cts.Token;

        try
        {
            IsFindingSun = true;
            OpenFindSunLog();

            var (raHours, decDeg) = SunPosition.GetApparentRaDecJNow(DateTime.UtcNow);

            StatusText = $"Find Sun: slewing to RA {raHours:F3}h, Dec {decDeg:F2}°…";
            _findSunLog?.Info($"Slewing to computed Sun position RA {raHours:F5}h, Dec {decDeg:F4}° (camera live: {IsLive && _connectedCamera is not null}).");
            await _mount.SlewToCoordinatesAsync(raHours, decDeg, ct);
            StatusText = $"Find Sun: slewed to the computed position (RA {raHours:F3}h, Dec {decDeg:F2}°).";
            await LogMountPositionAsync("After slew", raHours, decDeg);

            if (!(IsLive && _connectedCamera is not null))
            {
                StatusText += " No live camera - stopping here.";
                return;
            }

            var fineTune = MessageBox.Show(
                "Slewed to the Sun's computed position. Search for the Sun and centre it using the live camera view?\n\n"
                + "If the view is dark this will change the camera's exposure and gain, and move the mount in a spiral around this position.",
                "Find Sun",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;

            if (!fineTune)
            {
                StatusText = "Find Sun: done (camera fine-tune skipped).";
                return;
            }

            StatusText = "Find Sun: checking the live view for signal…";
            var outcome = await RunFineTuneAsync(ct);
            var settingsNote = $" (Exposure {ExposureMicroseconds / 1000.0:0.###}ms, gain {Gain:0}.)";
            StatusText = outcome switch
            {
                FineTuneOutcome.Improved => "Find Sun: Sun located and centred on the live view." + settingsNote,
                FineTuneOutcome.NoImprovement => "Find Sun: Sun located; centring found no better position." + settingsNote,
                FineTuneOutcome.NoSignal => "Find Sun: signal found, but brightness never changed measurably as the mount moved "
                     + "(the frame may be saturated or showing sky, not the Sun). Check the live view." + settingsNote,
                _ => "Find Sun: no Sun found within the search area. Camera settings restored; the mount is back at the computed "
                     + "position. Check the mount's alignment and that the slit is uncovered."
            };
            _findSunLog?.Info($"Fine-tune outcome: {outcome}.");

            if (outcome == FineTuneOutcome.SunNotFound || outcome == FineTuneOutcome.NoSignal)
            {
                // Syncing the mount to the ephemeris position only makes sense when the Sun really is centred.
                return;
            }

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

            await LogMountPositionAsync("Before sync", null, null);
            var (syncRaHours, syncDecDeg) = await SyncMountToSunPositionAsync();
            _findSunLog?.Info($"Synced mount to RA {syncRaHours:F5}h, Dec {syncDecDeg:F4}°.");
            StatusText = $"Find Sun: mount synced to RA {syncRaHours:F3}h, Dec {syncDecDeg:F2}°.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Find Sun: cancelled.";
            _findSunLog?.Info("Cancelled by the user.");
            await StopMountMotionAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Find Sun failed: {ex.Message}";
            _findSunLog?.Error($"Find Sun failed: {ex}");
            await StopMountMotionAsync();
        }
        finally
        {
            _findSunCts = null;
            IsFindingSun = false;
            if (_findSunLog is not null)
            {
                StatusText += $" (Log: {_findSunLogPath})";
                _findSunLog.Dispose();
                _findSunLog = null;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelFindSun))]
    private void CancelFindSun()
    {
        StatusText = "Find Sun: cancelling…";
        _findSunCts?.Cancel();
    }

    private bool CanCancelFindSun => IsFindingSun;

    /// <summary>Best-effort stop of any mount motion - used when Find Sun is cancelled or fails mid-run.</summary>
    private async Task StopMountMotionAsync()
    {
        try { await _mount.AbortSlewAsync(); } catch { /* best effort */ }
        try { await _mount.MoveAxisAsync(TelescopeAxis.Primary, 0); } catch { /* best effort */ }
        try { await _mount.MoveAxisAsync(TelescopeAxis.Secondary, 0); } catch { /* best effort */ }
    }

    private enum FineTuneOutcome
    {
        /// <summary>The Sun was located and centring moved things to a better position.</summary>
        Improved,
        /// <summary>The Sun was located; brightness/centring varied but nothing beat the position it started at.</summary>
        NoImprovement,
        /// <summary>Signal was present but never changed by more than noise wherever the mount went - it
        /// carries no positional information (saturated frame, sky glow, ...).</summary>
        NoSignal,
        /// <summary>The spiral search covered its whole area without finding the Sun.</summary>
        SunNotFound
    }

    /// <summary>A brightness reading: the mean of several fresh preview frames' average values and how
    /// noisy that is, plus the frame-shape figures Find Sun's other decisions need (saturation, and
    /// where the light is concentrated). Centre values are NaN when no frame had a usable centroid.</summary>
    private readonly record struct BrightnessMeasurement(
        double Mean, double StdError, double FrameStdDev, int Frames, double MaxValue, int BitDepth,
        double SaturatedFraction, double CentreX, double CentreY)
    {
        public double FullScale => BitDepth > 0 ? Math.Pow(2, BitDepth) - 1 : 0;
        public bool HasCentre => !double.IsNaN(CentreX);
    }

    /// <summary>Running range of every measurement made in one fine-tune run - used to tell "no usable
    /// signal" (never varied beyond noise) from "already at the peak" (varied, but the start was best).</summary>
    private sealed class SignalRange
    {
        public double Min = double.MaxValue;
        public double Max = double.MinValue;
        public double MaxStdError;

        public void Add(BrightnessMeasurement m)
        {
            Min = Math.Min(Min, m.Mean);
            Max = Math.Max(Max, m.Mean);
            MaxStdError = Math.Max(MaxStdError, m.StdError);
        }

        public bool IsFlat => Max - Min <= Math.Max(FindSunNoiseSigmas * MaxStdError, FindSunMinRelativeImprovement * Max);
    }

    // Search / auto-exposure / centring tuning - all first guesses, to be adjusted from real logs.
    private const double FindSunSearchExposureMicroseconds = 500_000;
    private const double FindSunSearchGainFraction = 0.75; // of the camera's own gain range
    private const double FindSunSearchStepDeg = 0.35; // a little under the Sun's ~0.53° diameter, so a slit line can't slip between points
    private const int FindSunSearchMaxRings = 6; // +/- 2.1° each way = up to 168 points
    private const double FindSunSearchRateFraction = 0.15; // of max slew rate; faster than the fine-tune nudges
    private const int FindSunSearchFramesPerPoint = 2;
    private const double FindSunSaturatedFractionLimit = 0.005; // >0.5% of pixels at full scale = saturated (hot pixels are far fewer)
    private const double FindSunSearchDetectFractionOfFullScale = 0.02;
    private const double FindSunSearchDetectSigmas = 6;
    private const double FindSunSearchBaselineMaxFraction = 0.08;
    // If the search-settings frame is so bright it needs cutting by more than this to be usable, the
    // Sun is already on the slit: sky glow alone is nowhere near that much brighter than the user's
    // own (dark) starting frame, the Sun through the slit is thousands of times brighter.
    private const double FindSunAlreadyOnSunReductionFactor = 30;
    private const double FindSunTargetMeanFraction = 0.12;
    private const double FindSunDimMeanFraction = 0.04;
    private const double FindSunBrightMeanFraction = 0.30;
    private const double FindSunExposureCeilingMicroseconds = 100_000; // keeps measurements quick once the Sun is found
    private const int FindSunMaxAutoExposeIterations = 8;
    private const double FindSunGainUnitsPerDecade = 200; // ASI gain is in 0.1 dB units: +200 = x10 (a first guess for other cameras; the loop iterates anyway)
    private const double FindSunAxisCalibrationStepDeg = 0.15;
    private const double FindSunMinCalibrationShift = 0.02; // fraction of frame width the along-slit axis must move the centroid
    private const double FindSunCentreToleranceFraction = 0.02;
    private const int FindSunMaxCentreIterations = 4;
    private const double FindSunMaxCentreMoveDeg = 1.0;

    private string? _findSunLogPath;
    private CancellationTokenSource? _findSunCts;

    private void OpenFindSunLog()
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolScan", "logs");
            _findSunLogPath = Path.Combine(folder, $"FindSun_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            _findSunLog = ProcessingLog.OpenAt(_findSunLogPath);
            _findSunLog.Info($"Find Sun started. Mount connected: {IsMountConnected}; tracking: {_mountState.IsTracking}.");
            _findSunLog.Info(
                $"Camera: {_connectedCamera?.Name ?? "(none)"}; live: {IsLive}; gain {Gain:0} (range {MinGain:0}-{MaxGain:0}){(IsGainAuto ? " (auto)" : "")}; "
                + $"exposure {ExposureMicroseconds / 1000.0:0.###}ms{(IsExposureAuto ? " (auto)" : "")}; "
                + $"{SelectedColorSpace}, bin {SelectedBinning}, ROI {RoiWidth}x{RoiHeight}.");
        }
        catch
        {
            // A diagnostic log must never stop Find Sun itself from working.
            _findSunLog = null;
            _findSunLogPath = null;
        }
    }

    /// <summary>Logs where the mount says it's pointing now and (if given) how far that is from the
    /// intended target - the readout is one of the few objective checks that a slew/nudge really moved.</summary>
    private async Task LogMountPositionAsync(string label, double? targetRaHours, double? targetDecDeg)
    {
        if (_findSunLog is null)
            return;

        try
        {
            var (ra, dec) = await _mount.GetCurrentPositionAsync();
            var line = $"{label}: mount reports RA {ra:F5}h, Dec {dec:F4}°";
            if (targetRaHours is { } tRa && targetDecDeg is { } tDec)
            {
                var raErrArcsec = (ra - tRa) * 15 * 3600 * Math.Cos(tDec * Math.PI / 180);
                var decErrArcsec = (dec - tDec) * 3600;
                line += $" (target error: RA {raErrArcsec:+0;-0;0}\", Dec {decErrArcsec:+0;-0;0}\")";
            }
            _findSunLog.Info(line + $"; tracking {_mountState.IsTracking}, slewing {_mountState.IsSlewing}.");
        }
        catch (Exception ex)
        {
            _findSunLog.Error($"{label}: could not read mount position: {ex.Message}");
        }
    }

    // -----------------------------
    // Camera settings helpers
    // -----------------------------

    /// <summary>Sets exposure and/or gain directly (the view model's setters clamp/quantize and push to
    /// the camera). Auto is switched off first - a camera-driven value would fight these.</summary>
    private void ApplyCameraSettings(double? exposureMicroseconds, double? gain)
    {
        if (IsExposureAuto) IsExposureAuto = false;
        if (IsGainAuto) IsGainAuto = false;
        if (exposureMicroseconds is { } e)
            ExposureMicroseconds = Math.Clamp(e, ExposureScale.MinMicroseconds, ExposureScale.MaxMicroseconds);
        if (gain is { } g)
            Gain = Math.Clamp(g, MinGain, MaxGain);
    }

    /// <summary>Scales the camera's overall sensitivity by roughly <paramref name="factor"/>: exposure
    /// first (up to <see cref="FindSunExposureCeilingMicroseconds"/> when brightening), then gain for
    /// whatever exposure couldn't absorb. Returns the factor actually achieved (1 = at a limit, nothing changed).</summary>
    private double ApplyBrightnessFactor(double factor)
    {
        var exposure = ExposureMicroseconds;
        var gain = Gain;

        var newExposure = Math.Clamp(
            exposure * factor, ExposureScale.MinMicroseconds, Math.Max(FindSunExposureCeilingMicroseconds, exposure));
        var exposureFactor = newExposure / exposure;

        var leftover = factor / exposureFactor;
        var newGain = Math.Abs(leftover - 1) > 0.05
            ? Math.Clamp(gain + FindSunGainUnitsPerDecade * Math.Log10(leftover), MinGain, MaxGain)
            : gain;
        var gainFactor = Math.Pow(10, (newGain - gain) / FindSunGainUnitsPerDecade);

        ApplyCameraSettings(newExposure, newGain);
        return exposureFactor * gainFactor;
    }

    /// <summary>Waits long enough after a mount move or a settings change for frames captured
    /// beforehand to have flushed through: at least 400ms, and two exposures (one to finish the frame
    /// in flight, one for the first good frame) - which matters at the 500ms search exposure.</summary>
    private Task SettleAsync(CancellationToken ct) =>
        Task.Delay(TimeSpan.FromMilliseconds(Math.Max(400, 2 * ExposureMicroseconds / 1000.0 + 200)), ct);

    // -----------------------------
    // The run
    // -----------------------------

    /// <summary>
    /// 1. If the live frame has no signal, set the search exposure/gain and spiral outward until it does
    ///    (<see cref="SearchForSunAsync"/>). 2. Bring exposure/gain into a usable range for the Sun
    ///    (<see cref="AutoExposeAsync"/>). 3. Centre it (<see cref="RefinePointingAsync"/>). The camera's
    /// original settings are put back if the Sun isn't found (or on cancel/failure); otherwise the tuned
    /// settings are left in place, since they're what suits the Sun.
    /// </summary>
    private async Task<FineTuneOutcome> RunFineTuneAsync(CancellationToken ct)
    {
        var originalExposure = ExposureMicroseconds;
        var originalGain = Gain;
        var originalExposureAuto = IsExposureAuto;
        var originalGainAuto = IsGainAuto;
        var keepTuned = false;
        _findSunMeasuringCentroid = true;

        try
        {
            double maxRateDegPerSec;
            try
            {
                maxRateDegPerSec = await _mount.GetMaxSlewRateDegPerSecAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // GetMaxSlewRateDegPerSecAsync already falls back internally on failure (see its own
                // doc comment) - reaching here means something else entirely went wrong; fall back the
                // same way HandControlViewModel does rather than aborting the whole fine-tune.
                maxRateDegPerSec = FindSunFallbackMaxSlewRateDegPerSec;
            }

            var nudgeRate = maxRateDegPerSec * FindSunNudgeRateFraction;
            _findSunLog?.Info(
                $"Mount max rate {maxRateDegPerSec:0.####}°/s; fine-tune nudge rate {nudgeRate:0.####}°/s, "
                + $"search rate {maxRateDegPerSec * FindSunSearchRateFraction:0.####}°/s.");

            var pre = await MeasureBrightnessAsync("Pre-check", ct);
            if (pre.Mean < pre.FullScale * FindSunDarkFractionOfFullScale)
            {
                _findSunLog?.Info(
                    $"No signal at the current settings (average {pre.Mean:0.##} = {pre.Mean / pre.FullScale:P2} of full scale, "
                    + $"limit {FindSunDarkFractionOfFullScale:P1}) - starting the spiral search.");

                var found = await SearchForSunAsync(maxRateDegPerSec, ct);
                if (!found)
                {
                    var (ra, dec) = SunPosition.GetApparentRaDecJNow(DateTime.UtcNow);
                    _findSunLog?.Info("Sun not found; slewing back to the computed position and restoring camera settings.");
                    StatusText = "Find Sun: not found - returning to the computed position…";
                    await _mount.SlewToCoordinatesAsync(ra, dec, ct);
                    await LogMountPositionAsync("After return slew", ra, dec);
                    return FineTuneOutcome.SunNotFound;
                }

                // Back to the user's own settings as the starting point for auto-exposure - the search
                // settings (500ms, high gain) are far too sensitive for the Sun itself.
                ApplyCameraSettings(originalExposure, originalGain);
                await SettleAsync(ct);
            }
            else
            {
                _findSunLog?.Info($"Signal present at the current settings (average {pre.Mean:0.##}) - no search needed.");
                ApplyCameraSettings(null, null); // switches Auto off, leaves the values
            }

            StatusText = "Find Sun: adjusting exposure and gain…";
            await AutoExposeAsync(ct);

            var outcome = await RefinePointingAsync(maxRateDegPerSec, ct);
            keepTuned = true;
            return outcome;
        }
        finally
        {
            _findSunMeasuringCentroid = false;
            if (!keepTuned)
            {
                ApplyCameraSettings(originalExposure, originalGain);
                IsExposureAuto = originalExposureAuto;
                IsGainAuto = originalGainAuto;
                _findSunLog?.Info($"Restored camera settings: exposure {originalExposure / 1000.0:0.###}ms, gain {originalGain:0}.");
            }
            else
            {
                _findSunLog?.Info($"Leaving camera settings as tuned: exposure {ExposureMicroseconds / 1000.0:0.###}ms, gain {Gain:0} (auto off).");
            }
        }
    }

    /// <summary>
    /// Square spiral outward from the current pointing, one <see cref="FindSunSearchStepDeg"/> step at a
    /// time (Primary/Secondary axis moves, so it doesn't depend on which way the slit runs), measuring
    /// each point at the search exposure/gain. Stops at the first point that's clearly brighter than the
    /// baseline (confirmed by a second measurement). Returns false, at some offset from the start, if the
    /// whole area is covered without a detection - the caller returns the mount.
    /// </summary>
    private async Task<bool> SearchForSunAsync(double maxRateDegPerSec, CancellationToken ct)
    {
        var rate = maxRateDegPerSec * FindSunSearchRateFraction;
        var step = FindSunSearchStepDeg;

        var searchGain = MinGain + FindSunSearchGainFraction * (MaxGain - MinGain);
        ApplyCameraSettings(FindSunSearchExposureMicroseconds, searchGain);
        _findSunLog?.Info(
            $"Search settings: exposure {ExposureMicroseconds / 1000.0:0.###}ms, gain {Gain:0}; step {step}°, "
            + $"up to +/-{FindSunSearchMaxRings * step:0.##}°.");
        StatusText = "Find Sun: no signal - searching (exposure and gain raised)…";
        await SettleAsync(ct);

        var baseline = await MeasureBrightnessAsync("Search baseline", ct, FindSunSearchFramesPerPoint + 1);

        // A frame already bright/saturated at these very sensitive settings is either sky glow or the
        // Sun itself. Cut the settings until it isn't; if that took a huge cut it can't have been sky.
        double totalReduction = 1;
        for (var i = 0; i < FindSunMaxAutoExposeIterations
            && (baseline.SaturatedFraction > FindSunSaturatedFractionLimit
                || baseline.Mean > baseline.FullScale * FindSunSearchBaselineMaxFraction); i++)
        {
            var achieved = ApplyBrightnessFactor(0.25);
            if (Math.Abs(achieved - 1) < 0.05)
                break;
            totalReduction /= achieved;
            _findSunLog?.Info($"Search baseline too bright (mean {baseline.Mean:0.##}, saturated {baseline.SaturatedFraction:P2}); "
                + $"settings cut x{1 / achieved:0.#} (total x{totalReduction:0.#}) -> exposure {ExposureMicroseconds / 1000.0:0.###}ms, gain {Gain:0}.");
            await SettleAsync(ct);
            baseline = await MeasureBrightnessAsync("Search baseline (reduced)", ct, FindSunSearchFramesPerPoint + 1);
        }

        if (totalReduction > FindSunAlreadyOnSunReductionFactor)
        {
            _findSunLog?.Info(
                $"The frame needed its sensitivity cut x{totalReduction:0.#} to be usable - far more than sky glow would need, "
                + "so the Sun is already on the slit. Skipping the spiral.");
            return true;
        }

        var dirs = new (int Dx, int Dy)[] { (1, 0), (0, 1), (-1, 0), (0, -1) };
        var maxPoints = ((2 * FindSunSearchMaxRings) + 1) * ((2 * FindSunSearchMaxRings) + 1) - 1;
        int x = 0, y = 0, visited = 0, dir = 0, leg = 1;

        while (visited < maxPoints)
        {
            for (var rep = 0; rep < 2 && visited < maxPoints; rep++)
            {
                for (var i = 0; i < leg && visited < maxPoints; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (dx, dy) = dirs[dir];
                    x += dx;
                    y += dy;
                    visited++;

                    StatusText = $"Find Sun: searching… point {visited}/{maxPoints} (offset {x * step:+0.0;-0.0;0}°, {y * step:+0.0;-0.0;0}°)";
                    await PulseAsync(dx != 0 ? TelescopeAxis.Primary : TelescopeAxis.Secondary, (dx != 0 ? dx : dy) * step, rate, ct);

                    var m = await MeasureBrightnessAsync($"Search {visited} ({x},{y})", ct, FindSunSearchFramesPerPoint);
                    if (!SunDetected(m, baseline))
                        continue;

                    var confirm = await MeasureBrightnessAsync($"Search {visited} confirm", ct, FindSunSearchFramesPerPoint);
                    if (SunDetected(confirm, baseline))
                    {
                        _findSunLog?.Info($"Signal found at search point {visited}: offset ({x * step:+0.##;-0.##;0}°, {y * step:+0.##;-0.##;0}°) (Primary, Secondary axes).");
                        await LogMountPositionAsync("At search hit", null, null);
                        return true;
                    }
                    _findSunLog?.Info("Detection not confirmed by the second measurement - carrying on.");
                }
                dir = (dir + 1) % 4;
            }
            leg++;
        }

        _findSunLog?.Info($"Search finished: {visited} points covered with no detection.");
        return false;
    }

    private bool SunDetected(BrightnessMeasurement m, BrightnessMeasurement baseline)
    {
        var threshold = Math.Max(
            FindSunSearchDetectSigmas * Math.Max(m.FrameStdDev, baseline.FrameStdDev),
            FindSunSearchDetectFractionOfFullScale * m.FullScale);
        return m.Mean - baseline.Mean > threshold;
    }

    /// <summary>Brings the live frame into a usable range for the Sun: not saturated (>0.5% of pixels at
    /// full scale) and with an average of roughly 4-30% of full scale, aiming for ~12%. Adjusts exposure
    /// first, then gain (see <see cref="ApplyBrightnessFactor"/>), re-measuring after each change.</summary>
    private async Task AutoExposeAsync(CancellationToken ct)
    {
        for (var i = 0; i < FindSunMaxAutoExposeIterations; i++)
        {
            var m = await MeasureBrightnessAsync($"Auto-expose {i}", ct);
            var meanFraction = m.Mean / m.FullScale;

            double factor;
            if (m.SaturatedFraction > FindSunSaturatedFractionLimit)
                factor = 0.25;
            else if (meanFraction < FindSunDimMeanFraction)
                factor = Math.Min(8, FindSunTargetMeanFraction / Math.Max(meanFraction, 1e-4));
            else if (meanFraction > FindSunBrightMeanFraction)
                factor = FindSunTargetMeanFraction / meanFraction;
            else
            {
                _findSunLog?.Info($"Exposure/gain fine: exposure {ExposureMicroseconds / 1000.0:0.###}ms, gain {Gain:0}, mean {meanFraction:P1} of full scale.");
                return;
            }

            var achieved = ApplyBrightnessFactor(factor);
            _findSunLog?.Info(
                $"Auto-expose: mean {meanFraction:P1}, saturated {m.SaturatedFraction:P2}; wanted x{factor:0.##}, achieved x{achieved:0.##} "
                + $"-> exposure {ExposureMicroseconds / 1000.0:0.###}ms, gain {Gain:0}.");
            if (Math.Abs(achieved - 1) < 0.05)
            {
                _findSunLog?.Info("Auto-expose: at an exposure/gain limit - stopping.");
                return;
            }
            await SettleAsync(ct);
        }
    }

    /// <summary>
    /// Centres the Sun. Brightness alone can't do it: through a slit, moving the disc *along* the slit
    /// leaves total brightness unchanged (seen on real hardware: RA nudges did nothing, Dec nudges did),
    /// so the along-slit axis is found by pulsing each axis and seeing which one shifts the brightness
    /// centroid sideways (<see cref="CalibrateAlongSlitAxisAsync"/>), and is then driven to put the
    /// centroid at the middle of the frame (<see cref="CentreAlongSlitAsync"/>). The other (across-slit)
    /// axis is hill-climbed on brightness, which peaks when the slit passes through the disc's centre.
    /// If the axes can't be told apart, both are just hill-climbed as before.
    /// </summary>
    private async Task<FineTuneOutcome> RefinePointingAsync(double maxRateDegPerSec, CancellationToken ct)
    {
        var nudgeRate = maxRateDegPerSec * FindSunNudgeRateFraction;
        var fastRate = maxRateDegPerSec * FindSunSearchRateFraction;
        var range = new SignalRange();
        var improved = false;

        StatusText = "Find Sun: working out which axis runs along the slit…";
        var along = await CalibrateAlongSlitAxisAsync(nudgeRate, ct);

        if (along is { } slit)
        {
            var across = slit.Axis == TelescopeAxis.Primary ? TelescopeAxis.Secondary : TelescopeAxis.Primary;
            StatusText = "Find Sun: centring across the slit (brightness)…";
            await AutoExposeAsync(ct);
            improved |= await ClimbAxisAsync(across, nudgeRate, FindSunCoarseStepDeg, range, ct);
            StatusText = "Find Sun: centring along the slit (position)…";
            improved |= await CentreAlongSlitAsync(slit.Axis, slit.SlopePerDeg, nudgeRate, fastRate, range, ct);
            StatusText = "Find Sun: fine-centring across the slit…";
            await AutoExposeAsync(ct);
            improved |= await ClimbAxisAsync(across, nudgeRate, FindSunFineStepDeg, range, ct);
            improved |= await CentreAlongSlitAsync(slit.Axis, slit.SlopePerDeg, nudgeRate, fastRate, range, ct);
        }
        else
        {
            StatusText = "Find Sun: fine-tuning on brightness…";
            await AutoExposeAsync(ct);
            improved |= await ClimbAxisAsync(TelescopeAxis.Primary, nudgeRate, FindSunCoarseStepDeg, range, ct);
            improved |= await ClimbAxisAsync(TelescopeAxis.Secondary, nudgeRate, FindSunCoarseStepDeg, range, ct);
            improved |= await ClimbAxisAsync(TelescopeAxis.Primary, nudgeRate, FindSunFineStepDeg, range, ct);
            improved |= await ClimbAxisAsync(TelescopeAxis.Secondary, nudgeRate, FindSunFineStepDeg, range, ct);
        }

        var final = await MeasureBrightnessAsync("Final", ct);
        _findSunLog?.Info(
            $"Final: mean {final.Mean:0.##}, brightness centre X {final.CentreX:0.000}, Y {final.CentreY:0.000} (0.5 = frame centre). "
            + $"Signal range over the run: min {range.Min:0.##}, max {range.Max:0.##}, flat: {range.IsFlat}.");
        await LogMountPositionAsync("Final position", null, null);

        return improved ? FineTuneOutcome.Improved
            : range.IsFlat && along is null ? FineTuneOutcome.NoSignal
            : FineTuneOutcome.NoImprovement;
    }

    /// <summary>Pulses each axis a known angle and watches how far the brightness centroid moves
    /// sideways. The axis with the big shift runs along the slit; the other barely moves it. Returns the
    /// axis and its slope (centroid fraction per degree), or null if it can't be told (no centroid, or
    /// no axis moved it clearly more than the other).</summary>
    private async Task<(TelescopeAxis Axis, double SlopePerDeg)?> CalibrateAlongSlitAxisAsync(double rate, CancellationToken ct)
    {
        var step = FindSunAxisCalibrationStepDeg;
        var shifts = new Dictionary<TelescopeAxis, double>();

        foreach (var axis in new[] { TelescopeAxis.Primary, TelescopeAxis.Secondary })
        {
            var before = await MeasureBrightnessAsync($"Calibrate {axis} before", ct);
            if (!before.HasCentre)
            {
                _findSunLog?.Info($"Calibration: no brightness centroid available ({axis}); can't tell the slit axis.");
                return null;
            }

            await PulseAsync(axis, step, rate, ct);
            var after = await MeasureBrightnessAsync($"Calibrate {axis} after", ct);
            await PulseAsync(axis, -step, rate, ct);
            if (!after.HasCentre)
            {
                _findSunLog?.Info($"Calibration: lost the brightness centroid after moving {axis}; can't tell the slit axis.");
                return null;
            }

            shifts[axis] = (after.CentreX - before.CentreX) / step;
            _findSunLog?.Info($"Calibration {axis}: centre X {before.CentreX:0.000} -> {after.CentreX:0.000} for {step}° = {shifts[axis]:+0.000;-0.000} per degree.");
        }

        var primary = Math.Abs(shifts[TelescopeAxis.Primary]);
        var secondary = Math.Abs(shifts[TelescopeAxis.Secondary]);
        var (axisChosen, big, small) = primary >= secondary
            ? (TelescopeAxis.Primary, primary, secondary)
            : (TelescopeAxis.Secondary, secondary, primary);

        if (big * step < FindSunMinCalibrationShift || big < 3 * small)
        {
            _findSunLog?.Info($"Calibration inconclusive (shifts {primary:0.000} vs {secondary:0.000} per degree) - falling back to brightness only.");
            return null;
        }

        _findSunLog?.Info($"Slit runs along the {axisChosen} axis ({shifts[axisChosen]:+0.000;-0.000} centroid-fraction per degree).");
        return (axisChosen, shifts[axisChosen]);
    }

    /// <summary>Drives <paramref name="axis"/> until the brightness centroid sits at the middle of the
    /// frame (proportional moves from the calibrated slope, re-measured each time). Returns whether it moved.</summary>
    private async Task<bool> CentreAlongSlitAsync(
        TelescopeAxis axis, double slopePerDeg, double slowRate, double fastRate, SignalRange range, CancellationToken ct)
    {
        var moved = false;
        for (var i = 0; i < FindSunMaxCentreIterations; i++)
        {
            var m = await MeasureBrightnessAsync($"Centre {axis} {i}", ct);
            range.Add(m);
            if (!m.HasCentre)
            {
                _findSunLog?.Info("Centring: no brightness centroid - stopping.");
                return moved;
            }

            var error = m.CentreX - 0.5;
            if (Math.Abs(error) < FindSunCentreToleranceFraction)
            {
                _findSunLog?.Info($"Centred along the slit: centre X {m.CentreX:0.000} (within {FindSunCentreToleranceFraction:0.##} of the middle).");
                return moved;
            }

            var moveDeg = Math.Clamp(-error / slopePerDeg, -FindSunMaxCentreMoveDeg, FindSunMaxCentreMoveDeg);
            _findSunLog?.Info($"Centring {axis}: centre X {m.CentreX:0.000} (error {error:+0.000;-0.000}) -> move {moveDeg:+0.###;-0.###}°.");
            await PulseAsync(axis, moveDeg, Math.Abs(moveDeg) > 0.3 ? fastRate : slowRate, ct);
            moved = true;
        }

        var last = await MeasureBrightnessAsync($"Centre {axis} last", ct);
        _findSunLog?.Info($"Centring stopped after {FindSunMaxCentreIterations} moves: centre X {last.CentreX:0.000}.");
        return moved;
    }

    /// <summary>
    /// One-dimensional brightness hill-climb on a single mount axis, in steps of <paramref name="stepDeg"/>:
    /// steps in one direction while brightness keeps improving (beyond noise - see
    /// <see cref="IsBetter"/>), backs off the final non-improving step so the axis ends up at the peak
    /// rather than one step past it, and tries the opposite direction if the very first step didn't
    /// help at all. Bounded by <see cref="FindSunMaxStepsPerAxis"/> so a flat/noisy signal can't loop
    /// indefinitely. Returns whether the axis ended up somewhere brighter than it started.
    /// </summary>
    private async Task<bool> ClimbAxisAsync(TelescopeAxis axis, double rateDegPerSec, double stepDeg, SignalRange range, CancellationToken ct)
    {
        _findSunLog?.Info($"--- Climb {axis}, step {stepDeg}° ---");

        var best = await MeasureBrightnessAsync($"{axis} start", ct);
        range.Add(best);

        var direction = 1.0;
        await PulseAsync(axis, direction * stepDeg, rateDegPerSec, ct);
        var first = await MeasureBrightnessAsync($"{axis} step +1", ct);
        range.Add(first);

        if (!IsBetter(first, best))
        {
            // That direction didn't help - go past the start to the other side in one move (undo + one step).
            direction = -1.0;
            await PulseAsync(axis, direction * 2 * stepDeg, rateDegPerSec, ct);
            first = await MeasureBrightnessAsync($"{axis} step -1", ct);
            range.Add(first);

            if (!IsBetter(first, best))
            {
                // Neither direction helped - return to where the axis started and give up on it for this pass.
                await PulseAsync(axis, -direction * stepDeg, rateDegPerSec, ct);
                _findSunLog?.Info($"{axis}: neither direction improved on the start position; returned to it.");
                return false;
            }
        }

        best = first;
        for (var step = 2; step <= FindSunMaxStepsPerAxis; step++)
        {
            await PulseAsync(axis, direction * stepDeg, rateDegPerSec, ct);
            var after = await MeasureBrightnessAsync($"{axis} step {(direction > 0 ? "+" : "-")}{step}", ct);
            range.Add(after);
            if (!IsBetter(after, best))
            {
                // Overshot the peak - undo this last step and stop.
                await PulseAsync(axis, -direction * stepDeg, rateDegPerSec, ct);
                _findSunLog?.Info($"{axis}: passed the peak after {step - 1} step(s); backed off.");
                return true;
            }
            best = after;
        }

        _findSunLog?.Info($"{axis}: hit the {FindSunMaxStepsPerAxis}-step limit while still improving.");
        return true;
    }

    /// <summary>True when <paramref name="after"/> beats <paramref name="before"/> by more than noise:
    /// at least <see cref="FindSunNoiseSigmas"/> combined standard errors, and at least
    /// <see cref="FindSunMinRelativeImprovement"/> of the starting brightness.</summary>
    private bool IsBetter(BrightnessMeasurement after, BrightnessMeasurement before)
    {
        var noise = FindSunNoiseSigmas * Math.Sqrt(after.StdError * after.StdError + before.StdError * before.StdError);
        var threshold = Math.Max(noise, FindSunMinRelativeImprovement * before.Mean);
        var better = after.Mean - before.Mean > threshold;
        _findSunLog?.Detail(
            $"compare: {before.Mean:0.##} -> {after.Mean:0.##} (delta {after.Mean - before.Mean:+0.##;-0.##}, "
            + $"threshold {threshold:0.##}) => {(better ? "better" : "not better")}");
        return better;
    }

    /// <summary>Moves <paramref name="axis"/> by roughly <paramref name="signedDeg"/> (sign = direction)
    /// by running it at <paramref name="rateDegPerSec"/> for the matching duration, then stopping it.
    /// Timed, so the actual travel is only approximate - the log records the real elapsed time and the
    /// mount's own position readout before and after, to check the timing model against reality.
    /// Always sends a stop, even if cancelled or something throws mid-pulse.</summary>
    private async Task PulseAsync(TelescopeAxis axis, double signedDeg, double rateDegPerSec, CancellationToken ct)
    {
        var duration = TimeSpan.FromSeconds(Math.Abs(signedDeg) / rateDegPerSec);
        if (duration > FindSunMaxPulseDuration)
        {
            duration = FindSunMaxPulseDuration;
        }

        var sign = Math.Sign(signedDeg);
        (double RaHours, double DecDeg)? before = null;
        try { before = await _mount.GetCurrentPositionAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* logged below as missing */ }

        var stopwatch = new System.Diagnostics.Stopwatch();
        try
        {
            await _mount.MoveAxisAsync(axis, sign * rateDegPerSec, ct);
            stopwatch.Start();
            await Task.Delay(duration, ct);
        }
        finally
        {
            var elapsedAtStop = stopwatch.Elapsed;
            await _mount.MoveAxisAsync(axis, 0, CancellationToken.None);
            _findSunLog?.Info(
                $"Pulse {axis} {signedDeg:+0.###;-0.###}° at {sign * rateDegPerSec:+0.####;-0.####}°/s: "
                + $"planned {duration.TotalMilliseconds:0}ms, ran {elapsedAtStop.TotalMilliseconds:0}ms "
                + $"(~{rateDegPerSec * elapsedAtStop.TotalSeconds:0.####}° by timing).");
        }

        await SettleAsync(ct);

        if (_findSunLog is not null)
        {
            if (before is { } b)
            {
                try
                {
                    var (ra, dec) = await _mount.GetCurrentPositionAsync(ct);
                    var raArcsec = (ra - b.RaHours) * 15 * 3600 * Math.Cos(b.DecDeg * Math.PI / 180);
                    var decArcsec = (dec - b.DecDeg) * 3600;
                    _findSunLog.Detail(
                        $"mount readout moved RA {raArcsec:+0.#;-0.#;0}\", Dec {decArcsec:+0.#;-0.#;0}\" "
                        + $"(now RA {ra:F5}h, Dec {dec:F4}°)");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _findSunLog.Detail($"mount position readout after pulse failed: {ex.Message}");
                }
            }
            else
            {
                _findSunLog.Detail("mount position readout before pulse was unavailable.");
            }
        }
    }

    /// <summary>Waits for genuinely new preview frames (<paramref name="frames"/>, or a default that's
    /// smaller at long exposures) and returns their mean brightness with its noise, plus saturation and
    /// centroid. Gives up (with whatever it has) after a timeout so a stalled preview can't hang Find Sun.</summary>
    private async Task<BrightnessMeasurement> MeasureBrightnessAsync(string label, CancellationToken ct, int? frames = null)
    {
        var target = frames ?? (ExposureMicroseconds >= 200_000 ? 3 : FindSunFramesPerMeasurement);
        var means = new List<double>(target);
        var saturated = new List<double>(target);
        var centreXs = new List<double>(target);
        var centreYs = new List<double>(target);
        var lastSeen = _previewFrameCounter;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(3 + target * (1 + ExposureMicroseconds / 1_000_000.0));

        while (means.Count < target && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(15, ct);
            if (_previewFrameCounter != lastSeen)
            {
                lastSeen = _previewFrameCounter;
                means.Add(_lastFrameAverageBrightness);
                saturated.Add(_lastFrameSaturatedFraction);
                if (!double.IsNaN(_lastFrameCentreX))
                    centreXs.Add(_lastFrameCentreX);
                if (!double.IsNaN(_lastFrameCentreY))
                    centreYs.Add(_lastFrameCentreY);
            }
        }

        if (means.Count == 0)
        {
            // No fresh preview frame at all - fall back to the last value so the caller still has a number.
            means.Add(_lastFrameAverageBrightness);
            saturated.Add(_lastFrameSaturatedFraction);
            if (!double.IsNaN(_lastFrameCentreX))
                centreXs.Add(_lastFrameCentreX);
            if (!double.IsNaN(_lastFrameCentreY))
                centreYs.Add(_lastFrameCentreY);
            _findSunLog?.Error($"{label}: no new preview frames arrived within {timeout.TotalSeconds:0}s - using the last value.");
        }

        var mean = means.Average();
        var variance = means.Count > 1 ? means.Sum(s => (s - mean) * (s - mean)) / (means.Count - 1) : 0;
        var result = new BrightnessMeasurement(
            mean, Math.Sqrt(variance / means.Count), Math.Sqrt(variance), means.Count, _lastFrameMaxValue, _lastFrameBitDepth,
            saturated.Average(),
            centreXs.Count > 0 ? centreXs.Average() : double.NaN,
            centreYs.Count > 0 ? centreYs.Average() : double.NaN);

        _findSunLog?.Info(
            $"Brightness [{label}]: mean {mean:0.##} +/- {result.StdError:0.###} ({mean / Math.Max(1, result.FullScale):P2} of full scale; "
            + $"n={means.Count} in {stopwatch.ElapsedMilliseconds}ms, ~{means.Count * 1000.0 / Math.Max(1, stopwatch.ElapsedMilliseconds):0.#} frames/s); "
            + $"frame max {_lastFrameMaxValue:0}, saturated pixels {result.SaturatedFraction:P2}; "
            + $"centre X {result.CentreX:0.000} Y {result.CentreY:0.000}; exposure {ExposureMicroseconds / 1000.0:0.###}ms gain {Gain:0}.");
        return result;
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

            // Set before Gain (below) so a saved/default value is already clamped/quantized (see
            // OnGainChanged) against this camera's own real range, not whatever the previous camera's
            // range happened to be.
            MinGain = SelectedCamera.MinGain;
            MaxGain = SelectedCamera.MaxGain;

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
        ResetAllBestFocus(); // a "best" carried over from a previous live-view session isn't meaningful for this one
        _lastConfidentSpectralRay = null; // same reasoning - a confident line from a previous session/line isn't meaningful for this one
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
        _lastSpectralAnalysisUtc = DateTime.MinValue;

        if (Volatile.Read(ref _spectralAnalysisInFlight) != 0)
        {
            StatusText = "Still analysing the previous frame - try again in a moment.";
            return;
        }

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
    /// <see cref="_lastConfidentSpectralRay"/> - the live overlay's own last confident identification,
    /// if any - rather than always null: see that field's own doc comment for why this, not offline
    /// identification against the (typically far more tightly cropped) recorded file itself, is where
    /// this gap actually gets closed.
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
            BuildMountPointingSnapshot(),
            _lastConfidentSpectralRay));
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
        CancelFindSunCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Clamps to <see cref="MinGain"/>-<see cref="MaxGain"/> and rounds to the nearest
    /// whole number - applied to <see cref="Gain"/> itself (not just how it's displayed), so a Slider
    /// drag can't leave the camera set to some arbitrary fractional gain the numeric box could never
    /// have been used to enter. Same "quantize-and-re-invoke" shape as
    /// <see cref="OnExposureMicrosecondsChanged"/>'s own <c>QuantizeToRange</c> call, just simpler:
    /// Gain has one plain range, not a dropdown of sub-ranges with their own units.</summary>
    partial void OnGainChanged(double value)
    {
        var quantized = Math.Round(Math.Clamp(value, MinGain, MaxGain), MidpointRounding.AwayFromZero);
        if (Math.Abs(quantized - value) > 0.01)
        {
            Gain = quantized;
            return;
        }

        if (_connectedCamera is not null && !_syncingFromDevice)
        {
            _connectedCamera.Gain = value;
        }
        PersistSettingsIfConnected();
    }

    [RelayCommand]
    private void IncrementGain() => Gain = Math.Min(Gain + 1, MaxGain);

    [RelayCommand]
    private void DecrementGain() => Gain = Math.Max(Gain - 1, MinGain);

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

    /// <summary>The collimator "Reset Best" button - clears its running best-so-far low-water mark, e.g.
    /// before starting a fresh collimator adjustment pass. The camera-focus aid has its own
    /// (<see cref="ResetBestLineWidthCommand"/>): the two are adjusted independently, so resetting one
    /// shouldn't throw away the other's best.</summary>
    [RelayCommand]
    private void ResetBestEdgeWidth()
    {
        _bestEdgeWidthPixels = double.PositiveInfinity;
        BestEdgeWidthText = "—";
        _recentEdgeWidthsPixels.Clear();
    }

    /// <summary>The camera-focus "Reset Best" button - see <see cref="ResetBestEdgeWidth"/>.</summary>
    [RelayCommand]
    private void ResetBestLineWidth()
    {
        _bestLineWidthPixels = double.PositiveInfinity;
        BestLineWidthText = "—";
        _recentLineWidthsPixels.Clear();
    }

    /// <summary>Resets both - called automatically whenever live view (re)starts (see
    /// <see cref="ToggleLiveViewAsync"/>): a "best" carried over from a previous session/camera/ROI isn't
    /// a meaningful target for a new one.</summary>
    private void ResetAllBestFocus()
    {
        ResetBestEdgeWidth();
        ResetBestLineWidth();
    }

    /// <summary>Rolling-median smoothing over the last <see cref="RecentEdgeWidthWindowSize"/> valid
    /// readings - a median rather than a mean so it resists an occasional bad frame (a burst of
    /// sensor noise, a stray reflection) the same way <see cref="FocusAnalyzer"/>'s own per-column
    /// median does, rather than letting one bad frame either flash a wrong number on screen or wrongly
    /// set a new "Best". Frames where <see cref="EdgeFocusStats.HasEdge"/> is false don't get added
    /// here at all - the window just keeps showing the last confident reading rather than being
    /// diluted by "no measurement" frames.</summary>
    private double SmoothEdgeWidth(double edgeWidthPixels) =>
        RollingMedian(_recentEdgeWidthsPixels, RecentEdgeWidthWindowSize, edgeWidthPixels);

    /// <summary>The camera-focus counterpart to <see cref="SmoothEdgeWidth"/>.</summary>
    private double SmoothLineWidth(double lineWidthPixels) =>
        RollingMedian(_recentLineWidthsPixels, RecentLineWidthWindowSize, lineWidthPixels);

    private static double RollingMedian(List<double> window, int windowSize, double newValue)
    {
        window.Add(newValue);
        if (window.Count > windowSize)
        {
            window.RemoveAt(0);
        }

        var sorted = window.OrderBy(v => v).ToList();
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
            // transition this is trying to measure. That's ~60-80ms per full-size frame on this
            // (single-flight) preview thread, so it only runs while something is actually showing it:
            // the Focus Aid Expander or the pop-out graph window. Null when skipped.
            var focusStats = IsFocusAidExpanded || _isFocusGraphOpen
                ? FocusAnalyzer.MeasureEdgeSteepness(frame)
                : (EdgeFocusStats?)null;

            // The camera-focus aid and the spectral overlay both need a curvature fit over the full
            // frame - hundreds of ms of work - so they run on their own worker rather than here: this
            // method is single-flight, and anything slow in it drops preview frames (see
            // TryStartSpectralAnalysis's own doc comment for the measured numbers).
            TryStartSpectralAnalysis(frame, stretchMaxDimension);

            // Find Sun's centring needs to know where the light sits in the frame, and its search /
            // auto-exposure need to know how much of it is saturated - see FindSunAsync.
            var centroid = _findSunMeasuringCentroid ? FrameCentroid.Measure(frame) : FrameCentroidStats.None;
            var histogramTotal = stats.Histogram.Sum(count => (long)count);
            var saturatedFraction = histogramTotal > 0 ? (double)stats.Histogram[^1] / histogramTotal : 0;

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
                _lastFrameMaxValue = stats.MaxValue;
                _lastFrameBitDepth = stats.BitDepth;
                _lastFrameSaturatedFraction = saturatedFraction;
                _lastFrameCentreX = centroid.CentreX;
                _lastFrameCentreY = centroid.CentreY;
                _previewFrameCounter++;
                if (focusStats is { } edgeFocus)
                {
                    if (edgeFocus.HasEdge)
                    {
                        var smoothedEdgeWidth = SmoothEdgeWidth(edgeFocus.EdgeWidthPixels);
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

                    if (_isFocusGraphOpen && edgeFocus.Detail is { } edgeDetail)
                    {
                        EdgeProfilePlot = BuildEdgePlot(edgeDetail);
                    }
                }
                RenderPreview(previewWidth, previewHeight, stretchedPixels);

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

    /// <summary>
    /// Starts a spectral-analysis pass (live line overlay and/or camera-focus aid) on its own worker if
    /// one is wanted, due, and none is already running - always on a *copy* of the frame.
    ///
    /// Why this isn't inline in <see cref="ProcessPreviewFrame"/>: measured on a full 3840x2160 frame in
    /// a Release build, the plain curvature fit alone took ~790ms (see <see cref="LiveCurvatureFitter"/>).
    /// <see cref="ProcessPreviewFrame"/> is single-flight, so anything that slow inside it silently
    /// drops every preview frame that arrives meanwhile - reported as a very laggy preview while
    /// adjusting focus even though the capture rate read fine. Here it only ever delays the analysis
    /// readouts themselves, never the preview.
    ///
    /// The copy matters because the ASI driver rotates through a small ring of reused frame buffers
    /// (see <c>AsiCameraDevice.CaptureLoop</c>) - fine for the millisecond-scale preview work, but an
    /// analysis this long could otherwise read a buffer the camera has since overwritten mid-measurement.
    /// The one shared curvature fit feeds both consumers, so having both on costs one fit, not two.
    /// </summary>
    private void TryStartSpectralAnalysis(CameraFrame frame, int stretchMaxDimension)
    {
        var now = DateTime.UtcNow;
        if (now - _lastSpectralAnalysisUtc < SpectralAnalysisInterval)
        {
            return;
        }

        // Only while the Focus Aid Expander is open for the camera-focus aid (it's the only place the
        // number is shown), and only once a live instrument is resolved for the overlay.
        var wantLineFocus = IsFocusAidExpanded || _isFocusGraphOpen;
        var instrument = ShowSpectralLineLabels || ShowSpectralColorBand ? _connectedInstrument : null;
        if (!wantLineFocus && instrument is null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _spectralAnalysisInFlight, 1, 0) != 0)
        {
            return;
        }

        _lastSpectralAnalysisUtc = now;

        // Pixel size prefers the connected camera's own real value, falling back to
        // SpectralOverlayFallbackPixelSizeMicrons when that's unknown - the normal case for a loaded
        // test image (see LoadTestImage), which isn't tied to any real camera at all.
        var pixelSizeMicrons = _connectedCameraProfile?.PixelSizeMicrons ?? SpectralOverlayFallbackPixelSizeMicrons;
        var binning = SelectedBinning;
        var wantLabels = ShowSpectralLineLabels;
        var wantBand = ShowSpectralColorBand;
        var frameCopy = frame with { Data = (byte[])frame.Data.Clone() };

        Task.Run(() => RunSpectralAnalysis(frameCopy, instrument, pixelSizeMicrons, binning, wantLabels, wantBand, wantLineFocus, stretchMaxDimension));
    }

    private void RunSpectralAnalysis(
        CameraFrame frame,
        SpectrographProfile? instrument,
        double pixelSizeMicrons,
        int binning,
        bool wantLabels,
        bool wantBand,
        bool wantLineFocus,
        int stretchMaxDimension)
    {
        try
        {
            SpectralLineFocusStats? lineFocusStats = null;
            List<SpectralLineLabel>? spectralLabels = null;
            GradientStopCollection? spectralGradientStops = null;
            string? spectralOverlayDiagnostics = null;

            // One fit shared by both consumers. A failure here (e.g. a degenerate frame) is reported
            // by each consumer below rather than breaking anything.
            QuadraticPolynomial? curvature = null;
            Exception? fitFailure = null;
            try
            {
                curvature = LiveCurvatureFitter.Fit(frame);
            }
            catch (Exception ex)
            {
                fitFailure = ex;
            }

            // Camera-focus aid - a failed pass is reported as "no line" so the UI doesn't sit on a stale reading.
            if (wantLineFocus)
            {
                try
                {
                    lineFocusStats = fitFailure is null
                        ? SpectralLineFocusAnalyzer.Measure(frame, curvature)
                        : new SpectralLineFocusStats(false, 0, 0);
                }
                catch (Exception)
                {
                    lineFocusStats = new SpectralLineFocusStats(false, 0, 0);
                }
            }

            // Live spectral-line overlay (labels + colour gradient band - see SpectralOverlayAnalyzer).
            if (instrument is not null)
            {
                try
                {
                    if (fitFailure is not null)
                    {
                        throw fitFailure;
                    }

                    var maxShiftPixels = Math.Max(1, frame.Height / 2) - 1;
                    var overlay = SpectralOverlayAnalyzer.Analyze(frame, instrument, pixelSizeMicrons, binning, maxShiftPixels, curvature: curvature);
                    var downsampleScale = FramePreview.ComputeDownsampleScale(frame.Width, frame.Height, stretchMaxDimension);

                    // See _lastConfidentSpectralRay's own doc comment - this is what actually closes
                    // the "no metadata for the scan" gap, by capturing a confident live identification
                    // (made against the wide, uncropped preview, where there's real spectral context)
                    // before a recording ever gets cropped down to just the studied line.
                    if (overlay.Identification.IdentifiedRay is { } confidentRay)
                    {
                        _lastConfidentSpectralRay = confidentRay;
                    }

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

                    if (wantLabels)
                    {
                        spectralLabels = BuildSpectralLineLabels(overlay, downsampleScale);
                    }
                    if (wantBand)
                    {
                        spectralGradientStops = BuildSpectralGradientStops(overlay, maxShiftPixels);
                    }
                }
                catch (Exception ex)
                {
                    // Best-effort - a failed overlay pass shouldn't break anything; the previous overlay
                    // stays on screen until the next successful pass. Still surfaced in the diagnostics
                    // text, though - a silently-skipped pass otherwise looks identical to "nothing
                    // changed" from the UI, hiding a real failure.
                    spectralOverlayDiagnostics = $"Overlay pass failed: {ex.Message}";
                }
            }

            _dispatcher.BeginInvoke(() =>
            {
                if (lineFocusStats is { } lineFocus)
                {
                    if (lineFocus.HasLine)
                    {
                        var smoothedLineWidth = SmoothLineWidth(lineFocus.FwhmPixels);
                        LineWidthText = $"{smoothedLineWidth:0.00} px";
                        LineDepthText = $"{lineFocus.DepthFraction:P0}";
                        if (smoothedLineWidth < _bestLineWidthPixels)
                        {
                            _bestLineWidthPixels = smoothedLineWidth;
                            BestLineWidthText = $"{_bestLineWidthPixels:0.00} px";
                        }
                    }
                    else
                    {
                        LineWidthText = "No line detected";
                        LineDepthText = "—";
                    }

                    if (_isFocusGraphOpen && lineFocus.Detail is { } lineDetail)
                    {
                        LineProfilePlot = BuildLinePlot(lineDetail);
                    }
                }

                // Only overwritten by a pass that actually produced them - otherwise the previous
                // overlay stays on screen rather than flickering empty between updates.
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
            });
        }
        finally
        {
            Interlocked.Exchange(ref _spectralAnalysisInFlight, 0);
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
