using SolScan.Core.Camera;
using static SolScan.Infrastructure.Camera.Asi.AsiNative;

namespace SolScan.Infrastructure.Camera.Asi;

/// <summary>
/// <see cref="ICameraDevice"/> for a ZWO ASI camera, driving <see cref="AsiNative"/>'s video-
/// capture API family. ASI's SDK is poll/blocking - once video capture is started, frames are
/// pulled with a bounded-wait native call rather than pushed via callback, so streaming runs a
/// dedicated loop <see cref="Thread"/> (not a pool task -
/// this blocks on a native call for as long as an exposure takes, repeatedly, for the life of the
/// stream) that calls it in a loop and raises <see cref="FrameCaptured"/> per frame. Contrast with
/// <see cref="Altair.AltairCameraDevice"/>, which is callback-driven instead - see the plan notes
/// in CLAUDE.md's N.I.N.A. entry for why the two vendors don't share one capture loop shape.
/// </summary>
public sealed class AsiCameraDevice : ICameraDevice
{
    private readonly int _cameraId;

    // Full sensor resolution (unbinned) - Width/Height below are the *current*, binned frame
    // dimensions actually being streamed, which shrink as Binning goes up.
    private int _nativeMaxWidth;
    private int _nativeMaxHeight;

    private int _width;
    private int _height;
    private int _bytesPerPixel;
    private int _bitDepth;
    private CameraOutputFormat _outputFormat = CameraOutputFormat.Mono16;
    private int _binning = 1;

    private Thread? _captureThread;
    private volatile bool _stopRequested;
    private bool _gainIsAuto;
    private bool _exposureIsAuto;
    private bool _bandwidthIsAuto;

    public AsiCameraDevice(int cameraId, string name, IReadOnlyList<int> supportedBinning)
    {
        _cameraId = cameraId;
        Name = name;
        SupportedBinning = supportedBinning;
    }

    public string Id => _cameraId.ToString();
    public string Name { get; }

    public bool IsConnected { get; private set; }
    public bool IsStreaming { get; private set; }

    public double Gain
    {
        get => GetControl(AsiControlType.Gain);
        set => SetControl(AsiControlType.Gain, value, _gainIsAuto);
    }

    public double ExposureMicroseconds
    {
        get => GetControl(AsiControlType.Exposure);
        set => SetControl(AsiControlType.Exposure, value, _exposureIsAuto);
    }

    public int UsbBandwidthPercent
    {
        get => (int)GetControl(AsiControlType.BandwidthOverload);
        set => SetControl(AsiControlType.BandwidthOverload, value, _bandwidthIsAuto);
    }

    /// <summary>ASI's per-control auto flag is set jointly with the value in one native call
    /// (there's no "just flip auto, leave the value alone" API) - so toggling this re-pushes
    /// whatever <see cref="Gain"/> currently reads back as, now with the auto flag set.</summary>
    public bool IsGainAuto
    {
        get => _gainIsAuto;
        set
        {
            _gainIsAuto = value;
            SetControl(AsiControlType.Gain, GetControl(AsiControlType.Gain), value);
        }
    }

    public bool IsExposureAuto
    {
        get => _exposureIsAuto;
        set
        {
            _exposureIsAuto = value;
            SetControl(AsiControlType.Exposure, GetControl(AsiControlType.Exposure), value);
        }
    }

    public bool IsUsbBandwidthAuto
    {
        get => _bandwidthIsAuto;
        set
        {
            _bandwidthIsAuto = value;
            SetControl(AsiControlType.BandwidthOverload, GetControl(AsiControlType.BandwidthOverload), value);
        }
    }

    public CameraOutputFormat OutputFormat => _outputFormat;
    public int Binning => _binning;
    public IReadOnlyList<int> SupportedBinning { get; }

    public long DroppedFrameCount
    {
        get
        {
            if (!IsConnected)
            {
                return 0;
            }

            GetDroppedFrames(_cameraId, out var dropped);
            return dropped;
        }
    }

    public event EventHandler<CameraFrame>? FrameCaptured;

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        Check(OpenCamera(_cameraId), nameof(OpenCamera));
        Check(InitCamera(_cameraId), nameof(InitCamera));
        Check(GetCameraProperty(out var info, _cameraId), nameof(GetCameraProperty));

        _nativeMaxWidth = info.MaxWidth;
        _nativeMaxHeight = info.MaxHeight;

        IsConnected = true;
        // First entry in SupportedBinning rather than a hardcoded 1 - matches whatever the camera
        // itself reports as its lowest supported binning factor (always 1 in practice for ASI, but
        // no reason to assume that rather than just asking).
        ApplyRoiFormat(CameraOutputFormat.Mono16, binning: SupportedBinning.Count > 0 ? SupportedBinning[0] : 1);

        // See AsiControlType.HighSpeedMode's doc comment - left entirely unset (at the camera's own
        // power-on default) prior to this, the likely explanation for a large live-view fps gap
        // against ASICap/SharpCap at otherwise-matching Gain/Exposure/USB Turbo settings. Set once
        // per connect, not tied to ApplyRoiFormat/output-format changes.
        SetControl(AsiControlType.HighSpeedMode, 1, isAuto: false);
    }, cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (IsStreaming)
        {
            StopStreamingAsync(cancellationToken).GetAwaiter().GetResult();
        }

        CloseCamera(_cameraId);
        IsConnected = false;
    }, cancellationToken);

    public async Task SetOutputFormatAsync(CameraOutputFormat outputFormat, int binning, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return;
        }

        var wasStreaming = IsStreaming;
        if (wasStreaming)
        {
            await StopStreamingAsync(cancellationToken);
        }

        await Task.Run(() => ApplyRoiFormat(outputFormat, binning), cancellationToken);

        if (wasStreaming)
        {
            await StartStreamingAsync(cancellationToken);
        }
    }

    /// <summary>The actual ROI-format reconfiguration: ASI sets pixel format and binning together
    /// in one native call, and the requested width/height must already be pre-divided by the
    /// binning factor (the SDK doesn't do that division itself).</summary>
    private void ApplyRoiFormat(CameraOutputFormat outputFormat, int binning)
    {
        _outputFormat = outputFormat;
        _binning = binning;
        _width = _nativeMaxWidth / binning;
        _height = _nativeMaxHeight / binning;

        var imageType = outputFormat == CameraOutputFormat.Mono16 ? AsiImageType.Raw16 : AsiImageType.Raw8;
        _bytesPerPixel = imageType == AsiImageType.Raw16 ? 2 : 1;

        // Confirmed empirically on real ASI678MM hardware via FramePreview.ComputeHistogramStats
        // (a saturated pixel reads back as 65520 = 4095 << 4): ZWO's RAW16 output left-shifts the
        // 12-bit ADC reading to occupy the *upper* 12 bits of the 16-bit word, not the lower ones -
        // the opposite of what an earlier version of this code assumed. So the container size
        // (16 bits, i.e. divide by 65535) is the correct normalization range for Mono16, not the
        // sensor's own ADC depth (12 bits, i.e. divide by 4095) - dividing by the smaller, wrong
        // value clips almost everything above ~6% of the true range straight to white. Mono8 mode
        // is unaffected either way (the ADC reading is genuinely truncated down to 8 bits there,
        // not just masked, so it always legitimately uses the full 0-255 range).
        _bitDepth = _bytesPerPixel * 8;

        Check(SetRoiFormat(_cameraId, _width, _height, binning, imageType), nameof(SetRoiFormat));
    }

    public Task StartStreamingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected || IsStreaming)
        {
            return Task.CompletedTask;
        }

        Check(StartVideoCapture(_cameraId), nameof(StartVideoCapture));
        _stopRequested = false;
        IsStreaming = true;

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = $"AsiCameraDevice[{_cameraId}] capture loop",
        };
        _captureThread.Start();
        return Task.CompletedTask;
    }

    public Task StopStreamingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsStreaming)
        {
            return Task.CompletedTask;
        }

        _stopRequested = true;
        _captureThread?.Join();
        _captureThread = null;
        StopVideoCapture(_cameraId);
        IsStreaming = false;
        return Task.CompletedTask;
    }

    private void CaptureLoop()
    {
        var buffer = new byte[_width * _height * _bytesPerPixel];

        while (!_stopRequested)
        {
            // Wait comfortably longer than the current exposure so a slow/long exposure doesn't
            // spuriously time out; ASIGetVideoData itself returns as soon as a frame is ready.
            var waitMs = (int)Math.Max(100, ExposureMicroseconds / 1000.0 * 2 + 500);
            var result = GetVideoData(_cameraId, buffer, buffer.Length, waitMs);
            if (result != AsiErrorCode.Success)
            {
                // Timeout is expected while nothing's crossing the slit yet, or between frames on
                // a slow exposure - just loop back around rather than treating it as fatal.
                continue;
            }

            var frame = new CameraFrame((byte[])buffer.Clone(), _width, _height, _bitDepth, DateTime.UtcNow);
            FrameCaptured?.Invoke(this, frame);
        }
    }

    private double GetControl(AsiControlType controlType)
    {
        if (!IsConnected)
        {
            return 0;
        }

        GetControlValue(_cameraId, controlType, out var value, out _);
        return value;
    }

    private void SetControl(AsiControlType controlType, double value, bool isAuto)
    {
        if (!IsConnected)
        {
            return;
        }

        SetControlValue(_cameraId, controlType, (int)value, isAuto ? 1 : 0);
    }

    private static void Check(AsiErrorCode errorCode, string call)
    {
        if (errorCode != AsiErrorCode.Success)
        {
            throw new InvalidOperationException($"{call} failed: {errorCode}");
        }
    }
}
