using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolScan.Core.Camera;
using SolScan.Core.Capture;

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

    private readonly ICameraDiscoveryService _discoveryService;
    private readonly Func<ISerWriter> _serWriterFactory;
    private readonly ICameraSettingsStore _cameraSettingsStore;
    private readonly IAppSettingsStore _appSettingsStore;
    private readonly StatusBarViewModel _statusBar;
    private readonly Dispatcher _dispatcher;
    private readonly Lock _recordingLock = new();

    private ICameraDevice? _connectedCamera;
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

    // 0 = idle, 1 = a background preview-processing Task is currently running - see
    // OnFrameCaptured/ProcessPreviewFrame. Interlocked rather than a plain bool since it's read and
    // written from whichever thread the connected device raises FrameCaptured on.
    private int _previewProcessingInFlight;

    public ObservableCollection<ICameraDevice> AvailableCameras { get; } = [];

    [ObservableProperty]
    private ICameraDevice? selectedCamera;

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private bool isLive;

    [ObservableProperty]
    private bool isRecording;

    /// <summary>Raw gain, 0-600 (0.1dB/step) - the ASI678MM's own range, matching what SharpCap
    /// shows for it (see <see cref="ICameraDevice.Gain"/>).</summary>
    [ObservableProperty]
    private double gain = 150;

    [ObservableProperty]
    private bool isGainAuto;

    [ObservableProperty]
    private double exposureMicroseconds = 10_000;

    /// <summary>0-1 slider position on <see cref="ExposureScale"/>'s log curve - what
    /// CaptureView.xaml's exposure Slider actually binds to, since exposure needs to span
    /// 0.032ms-5s and a linear slider can't usefully cover that range (see
    /// <see cref="ExposureScale"/>'s doc comment). Kept in sync with
    /// <see cref="ExposureMicroseconds"/> in both directions via <see cref="_syncingExposureLink"/>.</summary>
    [ObservableProperty]
    private double exposureSliderPosition = ExposureScale.ToSliderPosition(10_000);

    [ObservableProperty]
    private string exposureDisplayText = ExposureScale.Format(10_000);

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
        StatusBarViewModel statusBar)
    {
        _discoveryService = discoveryService;
        _serWriterFactory = serWriterFactory;
        _cameraSettingsStore = cameraSettingsStore;
        _appSettingsStore = appSettingsStore;
        _statusBar = statusBar;
        _dispatcher = Dispatcher.CurrentDispatcher;
        RefreshCameras();
    }

    private bool CanToggleLiveView => SelectedCamera is not null && !IsRecording;
    private bool CanDisconnect => IsConnected && !IsRecording;
    private bool CanStartRecording => IsLive && !IsRecording;

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
        StatusText = "Camera disconnected.";
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

    partial void OnIsLiveChanged(bool value) => StartRecordingCommand.NotifyCanExecuteChanged();

    partial void OnIsRecordingChanged(bool value)
    {
        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        StartRecordingCommand.NotifyCanExecuteChanged();
        StopRecordingCommand.NotifyCanExecuteChanged();
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
        if (_connectedCamera is not null && !_syncingFromDevice)
        {
            _connectedCamera.ExposureMicroseconds = value;
        }

        ExposureDisplayText = ExposureScale.Format(value);

        if (!_syncingExposureLink)
        {
            _syncingExposureLink = true;
            ExposureSliderPosition = ExposureScale.ToSliderPosition(value);
            _syncingExposureLink = false;
        }

        PersistSettingsIfConnected();
    }

    partial void OnExposureSliderPositionChanged(double value)
    {
        if (_syncingExposureLink)
        {
            return;
        }

        _syncingExposureLink = true;
        ExposureMicroseconds = ExposureScale.FromSliderPosition(value);
        _syncingExposureLink = false;
    }

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

    partial void OnRoiWidthChanged(int value) => ApplyOutputFormatChange();

    partial void OnRoiHeightChanged(int value) => ApplyOutputFormatChange();

    /// <summary>Resets the ROI back to the full sensor - the "Full Frame" button on CaptureView.xaml.
    /// 0 means "full frame" to the device regardless of the sensor's actual size (see
    /// <see cref="RoiWidth"/>'s doc comment), so there's no need to know real dimensions here.</summary>
    [RelayCommand]
    private void ResetRoi()
    {
        RoiWidth = 0;
        RoiHeight = 0;
    }

    partial void OnContrastBlackPointChanged(double value) => PersistSettingsIfConnected();

    partial void OnContrastWhitePointChanged(double value) => PersistSettingsIfConnected();

    partial void OnIsContrastAutoChanged(bool value) => PersistSettingsIfConnected();

    partial void OnSelectedColorSpaceChanged(CameraOutputFormat value) => ApplyOutputFormatChange();

    partial void OnSelectedBinningChanged(int value) => ApplyOutputFormatChange();

    /// <summary>Colour space, binning, and ROI are all reconfigured together (see
    /// <see cref="ICameraDevice.SetOutputFormatAsync"/>) - fired from any of the three controls'
    /// OnXChanged, which is why this is a fire-and-forget async void rather than an [RelayCommand]:
    /// it's reacting to a property change, not a user-invoked command.</summary>
    private async void ApplyOutputFormatChange()
    {
        if (_connectedCamera is null || _syncingFromDevice)
        {
            return;
        }

        if (IsRecording)
        {
            // Changing frame geometry/bit depth mid-file isn't representable in a SER file's fixed
            // header - revert the colour space/binning dropdowns rather than silently corrupting the
            // recording. RoiWidth/RoiHeight aren't reverted here - ICameraDevice has no readback
            // property for the currently-applied ROI to revert *to* (see its own doc comment) - but
            // CaptureView.xaml already disables the ROI controls while IsRecording, so this path
            // isn't expected to be hit via the ROI textboxes in practice.
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

    /// <summary>Saves the current dial-in state for <see cref="_connectedCamera"/>'s model (see
    /// <see cref="ICameraSettingsStore"/>) - a no-op while nothing's connected, or while a
    /// property is being set *from* the device rather than by the user (loading previously-saved
    /// settings back in on connect, or live auto-readback while an Auto flag is on - neither of
    /// those should immediately re-save what was just read).</summary>
    private void PersistSettingsIfConnected()
    {
        if (_connectedCamera is null || _syncingFromDevice)
        {
            return;
        }

        _cameraSettingsStore.Save(_connectedCamera.Name, new CameraSettings(
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
            RoiHeight));
    }

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
        if (RoiWidth <= 0 || RoiHeight <= 0)
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
            var (stretchedPixels, previewWidth, previewHeight) = FramePreview.Stretch(frame, blackPoint, whitePoint, stretchMaxDimension);
            var droppedFrames = camera?.DroppedFrameCount ?? 0;

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
