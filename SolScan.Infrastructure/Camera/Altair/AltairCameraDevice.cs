using SolScan.Core.Camera;
using static SolScan.Infrastructure.Camera.Altair.AltairNative;

namespace SolScan.Infrastructure.Camera.Altair;

/// <summary>
/// <see cref="ICameraDevice"/> for an Altair camera, driving <see cref="AltairNative"/>'s pull-mode
/// API. Unlike ZWO ASI (poll/blocking, see <see cref="Asi.AsiCameraDevice"/>), Altair's SDK is
/// callback-driven: <see cref="AltairNative.StartPullModeWithCallback"/> registers a native
/// callback that fires when a frame's ready, and the handler pulls it with
/// <see cref="AltairNative.PullImageV3"/> - no dedicated polling thread of our own needed, the
/// vendor SDK's own worker thread drives it.
/// </summary>
public sealed class AltairCameraDevice : ICameraDevice
{
    private IntPtr _handle;

    // Full sensor resolution (unbinned), captured once at connect - Width/Height below are the
    // *current*, binned frame dimensions actually being streamed.
    private int _nativeMaxWidth;
    private int _nativeMaxHeight;

    private int _width;
    private int _height;
    private int _bytesPerPixel = 2;
    private int _bitDepth = 16;
    private CameraOutputFormat _outputFormat = CameraOutputFormat.Mono16;
    private int _binning = 1;
    private uint _maxSpeed = 1;
    private uint _lastSequence;
    private long _droppedFrameCount;
    private byte[] _pullBuffer = [];

    // Kept as a field so the delegate isn't garbage-collected while the native side still holds a
    // pointer to it - a classic P/Invoke callback pitfall.
    private EventCallback? _eventCallback;

    public AltairCameraDevice(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }

    public bool IsConnected { get; private set; }
    public bool IsStreaming { get; private set; }

    public double Gain
    {
        get
        {
            if (!IsConnected)
            {
                return 0;
            }

            return GetExpoAGain(_handle, out var gain) >= 0 ? gain : 0;
        }
        set
        {
            if (IsConnected)
            {
                PutExpoAGain(_handle, (ushort)value);
            }
        }
    }

    public double ExposureMicroseconds
    {
        get
        {
            if (!IsConnected)
            {
                return 0;
            }

            return GetExpoTime(_handle, out var time) >= 0 ? time : 0;
        }
        set
        {
            if (IsConnected)
            {
                PutExpoTime(_handle, (uint)value);
            }
        }
    }

    /// <summary>Altair has no native 0-100 throttle - it exposes a discrete transfer-speed level
    /// instead ([0, MaxSpeed], closed interval), so this maps to/from that range.</summary>
    public int UsbBandwidthPercent
    {
        get
        {
            if (!IsConnected || _maxSpeed == 0)
            {
                return 0;
            }

            return GetSpeed(_handle, out var speed) >= 0 ? (int)(speed * 100 / _maxSpeed) : 0;
        }
        set
        {
            if (IsConnected && _maxSpeed > 0)
            {
                var speed = (ushort)Math.Clamp(value * _maxSpeed / 100, 0, _maxSpeed);
                PutSpeed(_handle, speed);
            }
        }
    }

    /// <summary>Altair has one auto-exposure flag governing exposure time and gain together - see
    /// <see cref="AltairNative.PutAutoExpoEnable"/> - so this and <see cref="IsExposureAuto"/> are
    /// the same underlying toggle, not two independent ones.</summary>
    public bool IsGainAuto
    {
        get => IsExposureAuto;
        set => IsExposureAuto = value;
    }

    public bool IsExposureAuto
    {
        get => IsConnected && GetAutoExpoEnable(_handle, out var isAuto) >= 0 && isAuto != 0;
        set
        {
            if (IsConnected)
            {
                PutAutoExpoEnable(_handle, value ? 1 : 0);
            }
        }
    }

    /// <summary>Altair's transfer-speed control has no auto mode - always false, and the setter is
    /// a no-op.</summary>
    public bool IsUsbBandwidthAuto
    {
        get => false;
        set { }
    }

    public CameraOutputFormat OutputFormat => _outputFormat;
    public int Binning => _binning;

    /// <summary>Not queried from the SDK - the real capability API exists
    /// (<c>Altaircam_get_BinningNumber</c>/<c>get_BinningValue</c>) but returns ANSI strings like
    /// "2x2" needing extra marshaling that wasn't worth adding for a vendor with no hardware to
    /// test against in this environment - a reasonable default matching common Altair mono camera
    /// capability. Revisit once real hardware is available.</summary>
    public IReadOnlyList<int> SupportedBinning { get; } = [1, 2, 3, 4];

    public long DroppedFrameCount => _droppedFrameCount;

    public event EventHandler<CameraFrame>? FrameCaptured;

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        _handle = Open(Id);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Altaircam_Open failed for device '{Id}'.");
        }

        GetSize(_handle, out _nativeMaxWidth, out _nativeMaxHeight);
        _maxSpeed = GetMaxSpeed(_handle);

        IsConnected = true;
        // First entry in SupportedBinning rather than a hardcoded 1 - see AsiCameraDevice's
        // equivalent for the reasoning (currently a no-op here since SupportedBinning is a
        // hardcoded [1,2,3,4] guess anyway - see its own doc comment).
        ApplyOutputFormat(CameraOutputFormat.Mono16, binning: SupportedBinning.Count > 0 ? SupportedBinning[0] : 1);
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

        await Task.Run(() => ApplyOutputFormat(outputFormat, binning), cancellationToken);

        if (wasStreaming)
        {
            await StartStreamingAsync(cancellationToken);
        }
    }

    private void ApplyOutputFormat(CameraOutputFormat outputFormat, int binning)
    {
        _outputFormat = outputFormat;
        _binning = binning;

        // Raw (undemosaiced) sensor data, not the SDK's own RGB conversion - matches ASI's
        // approach and what the SHG reconstruction pipeline needs.
        PutOption(_handle, Option.Raw, 1);
        PutOption(_handle, Option.BitDepth, outputFormat == CameraOutputFormat.Mono16 ? 1 : 0);

        // OPTION_BINNING's encoding is genuinely odd (confirmed against N.I.N.A.'s vendor-sourced
        // altaircam.cs's own doc comment for it): 0x01 = no binning, (0x80 | n) = "average" n*n
        // binning (bit depth unchanged - the alternative "add" modes change bit depth, which would
        // need threading through _bitDepth below too, so deliberately not used here).
        PutOption(_handle, Option.Binning, binning <= 1 ? 0x01 : 0x80 | binning);

        _bytesPerPixel = outputFormat == CameraOutputFormat.Mono16 ? 2 : 1;

        // Container size (16), not the sensor's own ADC depth - ToupTek-alike cameras are
        // generally understood to left-shift/scale their 16-bit raw output to fill the full
        // container, and this is now confirmed for ZWO ASI's RAW16 too (see AsiCameraDevice.
        // ApplyRoiFormat's comment - measured empirically on real ASI678MM hardware: a saturated
        // pixel reads back as 65520 = 4095 << 4, i.e. the 12-bit ADC value left-shifted into the
        // upper 12 bits of the 16-bit word). Still not verified against real Altair hardware
        // specifically in this environment - if a real Altair camera's histogram/preview looks
        // implausible (e.g. everything crushed dark, or clipping to white far too easily), this is
        // the first place to check.
        _bitDepth = outputFormat == CameraOutputFormat.Mono16 ? 16 : 8;

        // Computed rather than re-queried via GetSize: whether plain Altaircam_get_Size (as
        // opposed to the vendor SDK's separate get_FinalSize, "final size after ROI, rotate,
        // binning" - not bound here) reflects a just-applied binning change is unverified in this
        // environment, so this mirrors AsiCameraDevice's same manual division instead of trusting
        // that.
        _width = _nativeMaxWidth / binning;
        _height = _nativeMaxHeight / binning;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (IsStreaming)
        {
            StopStreamingAsync(cancellationToken).GetAwaiter().GetResult();
        }

        if (_handle != IntPtr.Zero)
        {
            Close(_handle);
            _handle = IntPtr.Zero;
        }

        IsConnected = false;
    }, cancellationToken);

    public Task StartStreamingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected || IsStreaming)
        {
            return Task.CompletedTask;
        }

        _pullBuffer = new byte[_width * _height * _bytesPerPixel];
        _lastSequence = 0;
        _droppedFrameCount = 0;
        _eventCallback = OnNativeEvent;

        var result = StartPullModeWithCallback(_handle, _eventCallback, IntPtr.Zero);
        if (result < 0)
        {
            throw new InvalidOperationException($"Altaircam_StartPullModeWithCallback failed: HRESULT {result:X}.");
        }

        IsStreaming = true;
        return Task.CompletedTask;
    }

    public Task StopStreamingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsStreaming)
        {
            return Task.CompletedTask;
        }

        IsStreaming = false;
        Stop(_handle);
        _eventCallback = null;
        return Task.CompletedTask;
    }

    /// <summary>Fires on the vendor SDK's own worker thread - already off the UI thread, so no
    /// extra thread of our own is needed for the pull-mode path (contrast
    /// <see cref="Asi.AsiCameraDevice"/>'s dedicated polling loop thread).</summary>
    private void OnNativeEvent(uint nEvent, IntPtr ctxCallback)
    {
        if (!IsStreaming || nEvent != (uint)Event.Image)
        {
            return;
        }

        if (PullImageV3(_handle, _pullBuffer, _bytesPerPixel * 8, 0, out var info) < 0)
        {
            return;
        }

        if (_lastSequence != 0 && info.Sequence > _lastSequence + 1)
        {
            _droppedFrameCount += info.Sequence - _lastSequence - 1;
        }
        _lastSequence = info.Sequence;

        var frame = new CameraFrame((byte[])_pullBuffer.Clone(), _width, _height, _bitDepth, DateTime.UtcNow);
        FrameCaptured?.Invoke(this, frame);
    }
}
