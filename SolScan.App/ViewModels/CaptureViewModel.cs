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
    private readonly StatusBarViewModel _statusBar;
    private readonly Dispatcher _dispatcher;
    private readonly Lock _recordingLock = new();

    private ICameraDevice? _connectedCamera;
    private ISerWriter? _activeWriter;
    private bool _writerNeedsOpening;
    private DateTime _lastPreviewRedrawUtc = DateTime.MinValue;

    // The most recently captured frame's full (un-cropped) dimensions - tracked so RoiWidth/
    // RoiHeight can be (re)defaulted to "full sensor" the first time a frame arrives, and again
    // whenever that geometry actually changes (a binning/colour-space change), without stomping a
    // user-chosen ROI that's smaller than the frame. Only ever touched from the capture thread in
    // OnFrameCaptured.
    private int _lastFullFrameWidth;
    private int _lastFullFrameHeight;

    // The ROI actually applied to the file currently being recorded - captured once, from the first
    // frame of the recording session (alongside _writerNeedsOpening below), rather than recomputed
    // from RoiWidth/RoiHeight on every frame. A SER file's header fixes its geometry for the whole
    // file, so the recording must keep using whatever ROI was in effect when it started even if the
    // user manages to change RoiWidth/RoiHeight mid-recording (CaptureView.xaml disables those
    // controls while IsRecording, but this is the guarantee, not the UI).
    private RoiRect _activeRecordingRoi;

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

    /// <summary>Desired ROI width in full-frame sensor pixels, centred on the sensor - see
    /// <see cref="FramePreview.ComputeCenteredRoi"/>. 0 (the default) means "not chosen yet/full
    /// frame": <see cref="OnFrameCaptured"/> fills this in with the actual sensor width the first
    /// time a frame arrives (and again after a binning/colour-space change), unless the user has
    /// already picked something smaller. Drives the histogram (ROI-only, see
    /// <see cref="ProcessPreviewFrame"/>), the ROI mask overlay, and - while recording - what's
    /// actually cropped and written to the SER file.</summary>
    [ObservableProperty]
    private int roiWidth;

    /// <summary>See <see cref="RoiWidth"/> - same idea, sensor height.</summary>
    [ObservableProperty]
    private int roiHeight;

    /// <summary>A filled "donut" - the full preview bitmap dimmed by a translucent overlay
    /// everywhere except the ROI itself, which is left unmasked - see
    /// <see cref="BuildRoiMaskGeometry"/> and CaptureView.xaml. Null (no overlay drawn) before the
    /// first preview frame.</summary>
    [ObservableProperty]
    private Geometry? roiMaskGeometry;

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
        StatusBarViewModel statusBar)
    {
        _discoveryService = discoveryService;
        _serWriterFactory = serWriterFactory;
        _cameraSettingsStore = cameraSettingsStore;
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
            // connect regardless of whether saved settings exist.
            AvailableBinningOptions.Clear();
            foreach (var binning in SelectedCamera.SupportedBinning)
            {
                AvailableBinningOptions.Add(binning);
            }

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
                SelectedBinning = savedSettings.Binning;
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

            SelectedCamera.Gain = Gain;
            SelectedCamera.ExposureMicroseconds = ExposureMicroseconds;
            SelectedCamera.UsbBandwidthPercent = UsbBandwidthPercent;
            SelectedCamera.IsGainAuto = IsGainAuto;
            SelectedCamera.IsExposureAuto = IsExposureAuto;
            SelectedCamera.IsUsbBandwidthAuto = IsUsbBandwidthAuto;

            if (SelectedCamera.OutputFormat != SelectedColorSpace || SelectedCamera.Binning != SelectedBinning)
            {
                await SelectedCamera.SetOutputFormatAsync(SelectedColorSpace, SelectedBinning);
            }
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

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SolScan", "Captures");
        Directory.CreateDirectory(directory);
        RecordingFilePath = Path.Combine(directory, $"SolScan_{DateTime.Now:yyyyMMdd_HHmmss}.ser");

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
        ISerWriter? writer;
        lock (_recordingLock)
        {
            writer = _activeWriter;
            _activeWriter = null;
        }

        writer?.Close();
        writer?.Dispose();
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

    partial void OnRoiWidthChanged(int value) => PersistSettingsIfConnected();

    partial void OnRoiHeightChanged(int value) => PersistSettingsIfConnected();

    /// <summary>Resets the ROI back to the full sensor - the "Full Frame" button on CaptureView.xaml.
    /// Falls back to 0 (also "full frame" - see <see cref="RoiWidth"/>'s doc comment) if no frame's
    /// been captured yet to know the real sensor dimensions from.</summary>
    [RelayCommand]
    private void ResetRoi()
    {
        RoiWidth = _lastFullFrameWidth;
        RoiHeight = _lastFullFrameHeight;
    }

    partial void OnContrastBlackPointChanged(double value) => PersistSettingsIfConnected();

    partial void OnContrastWhitePointChanged(double value) => PersistSettingsIfConnected();

    partial void OnIsContrastAutoChanged(bool value) => PersistSettingsIfConnected();

    partial void OnSelectedColorSpaceChanged(CameraOutputFormat value) => ApplyOutputFormatChange();

    partial void OnSelectedBinningChanged(int value) => ApplyOutputFormatChange();

    /// <summary>Colour space and binning are reconfigured together (see
    /// <see cref="ICameraDevice.SetOutputFormatAsync"/>) - fired from either dropdown's
    /// OnXChanged, which is why this is a fire-and-forget async void rather than an
    /// [RelayCommand]: it's reacting to a property change, not a user-invoked command.</summary>
    private async void ApplyOutputFormatChange()
    {
        if (_connectedCamera is null || _syncingFromDevice)
        {
            return;
        }

        if (IsRecording)
        {
            // Changing frame geometry/bit depth mid-file isn't representable in a SER file's fixed
            // header - revert the dropdown rather than silently corrupting the recording.
            StatusText = "Stop recording before changing colour space/binning.";
            _syncingFromDevice = true;
            SelectedColorSpace = _connectedCamera.OutputFormat;
            SelectedBinning = _connectedCamera.Binning;
            _syncingFromDevice = false;
            return;
        }

        try
        {
            await _connectedCamera.SetOutputFormatAsync(SelectedColorSpace, SelectedBinning);
            PersistSettingsIfConnected();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to apply colour space/binning: {ex.Message}";
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

        // (Re)default RoiWidth/RoiHeight to the full sensor the first time a frame arrives, and
        // again whenever the full-frame geometry actually changes (a binning/colour-space change) -
        // but only while the ROI was already tracking "full frame" (0, unset, or equal to the
        // *previous* full geometry), so a user-chosen smaller ROI survives a geometry change (it's
        // re-clamped per-frame by ComputeCenteredRoi below instead, not stomped here).
        if (frame.Width != _lastFullFrameWidth || frame.Height != _lastFullFrameHeight)
        {
            var wasTrackingFullFrame = RoiWidth <= 0 || RoiHeight <= 0
                || (RoiWidth == _lastFullFrameWidth && RoiHeight == _lastFullFrameHeight);
            _lastFullFrameWidth = frame.Width;
            _lastFullFrameHeight = frame.Height;
            if (wasTrackingFullFrame)
            {
                var fullWidth = frame.Width;
                var fullHeight = frame.Height;
                _dispatcher.BeginInvoke(() =>
                {
                    RoiWidth = fullWidth;
                    RoiHeight = fullHeight;
                });
            }
        }

        ISerWriter? writer;
        lock (_recordingLock)
        {
            writer = _activeWriter;
            if (writer is not null && _writerNeedsOpening)
            {
                _activeRecordingRoi = FramePreview.ComputeCenteredRoi(frame.Width, frame.Height, RoiWidth, RoiHeight);
                writer.Open(RecordingFilePath!, _activeRecordingRoi.Width, _activeRecordingRoi.Height, frame.BitDepth);
                _writerNeedsOpening = false;
            }
        }

        if (writer is not null)
        {
            writer.WriteFrame(FramePreview.CropToRoi(frame, _activeRecordingRoi));
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
            // The histogram (and, below, the auto-stretch derived from it) is driven from the ROI
            // only, not the whole frame - the point of an ROI is usually to frame the solar disk/
            // spectral line, and a surrounding dark bezel outside it would otherwise skew both.
            // CropToRoi is a no-op (returns frame itself) when the ROI covers the full frame, so
            // this costs nothing extra in the default, no-ROI-chosen case.
            var roi = FramePreview.ComputeCenteredRoi(frame.Width, frame.Height, RoiWidth, RoiHeight);
            var roiFrame = FramePreview.CropToRoi(frame, roi);
            var stats = FramePreview.ComputeHistogramStats(roiFrame);

            // Auto-stretch is computed fresh from *this* frame's histogram rather than read back
            // off the (UI-thread-owned) ContrastBlackPoint/WhitePoint properties, so the preview
            // reacts immediately rather than trailing a frame behind - see
            // FramePreview.ComputeAutoStretch's doc comment for why this exists at all (SharpCap
            // parity, not just cosmetic).
            var isContrastAuto = IsContrastAuto;
            var (blackPoint, whitePoint) = isContrastAuto
                ? FramePreview.ComputeAutoStretch(stats.Histogram)
                : (ContrastBlackPoint, ContrastWhitePoint);

            // The preview bitmap itself is still stretched from the *whole* frame, not the ROI crop
            // - the mask overlay built below dims everything outside the ROI directly on top of it,
            // rather than the live view only ever showing the cropped-down region.
            var (stretchedPixels, previewWidth, previewHeight) = FramePreview.Stretch(frame, blackPoint, whitePoint);
            var roiInPreview = FramePreview.ScaleRoiToPreview(roi, frame.Width, frame.Height, previewWidth, previewHeight);
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
                RoiMaskGeometry = BuildRoiMaskGeometry(previewWidth, previewHeight, roiInPreview);

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

    /// <summary>
    /// A rectangular "donut": the full <paramref name="previewWidth"/> x <paramref name="previewHeight"/>
    /// preview area filled, with <paramref name="roiInPreview"/> cut out of it as a hole - drawn on
    /// top of the (un-cropped) preview bitmap in CaptureView.xaml so the ROI itself shows through
    /// unmasked while everything around it is dimmed by the fill's own translucency. Two overlapping
    /// axis-aligned rectangles under an even-odd fill rule is the standard WPF way to punch a hole
    /// like this: a point inside the ROI crosses both rectangles' boundaries on the way out (even -
    /// unfilled), a point outside it but still inside the preview crosses only the outer one (odd -
    /// filled).
    /// </summary>
    private static Geometry BuildRoiMaskGeometry(int previewWidth, int previewHeight, RoiRect roiInPreview)
    {
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry(new Rect(0, 0, previewWidth, previewHeight)));
        group.Children.Add(new RectangleGeometry(new Rect(roiInPreview.X, roiInPreview.Y, roiInPreview.Width, roiInPreview.Height)));
        group.Freeze();
        return group;
    }

    /// <summary>
    /// Turns raw bucket counts into a filled step/bar-chart outline in a fixed
    /// [bucket count]x[0,100] space, normalized against the tallest bucket so the peak always
    /// reaches full height.
    ///
    /// Deliberately a *stepped* outline (each bucket gets a full-width flat-topped rectangle) and
    /// not a line connecting bucket-centre points: a heavily overexposed frame can have virtually
    /// every pixel land in one bucket (typically the last one), and a centre-point line would draw
    /// that as a triangle whose peak is a single, literally zero-width point - which, especially
    /// sitting right on the plot's own edge, can render as invisible (no fillable area) rather than
    /// a small sliver. A stepped bar always has real width, however extreme the spike.
    /// </summary>
    private static Geometry BuildHistogramGeometry(int[] rawHistogram)
    {
        const double PlotHeight = 100;
        var maxCount = rawHistogram.Length == 0 ? 0 : rawHistogram.Max();

        var figure = new PathFigure { StartPoint = new Point(0, PlotHeight), IsClosed = true };
        for (var i = 0; i < rawHistogram.Length; i++)
        {
            var ratio = maxCount == 0 ? 0 : rawHistogram[i] / (double)maxCount;
            var barTop = PlotHeight - (ratio * PlotHeight);
            figure.Segments.Add(new LineSegment(new Point(i, barTop), isStroked: true)); // rise/fall to this bucket's height
            figure.Segments.Add(new LineSegment(new Point(i + 1, barTop), isStroked: true)); // flat top across the full bucket width
        }
        figure.Segments.Add(new LineSegment(new Point(rawHistogram.Length, PlotHeight), isStroked: true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }
}
