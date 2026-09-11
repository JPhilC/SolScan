namespace SolScan.Core.Camera;

/// <summary>One captured frame's raw pixel data plus the geometry needed to interpret it.</summary>
public sealed record CameraFrame(byte[] Data, int Width, int Height, int BitDepth, DateTime TimestampUtc);

/// <summary>
/// The camera's output pixel format for streaming - SharpCap's "Colour Space" control. Mono8
/// trades precision for less USB bandwidth/higher achievable frame rate (the sensor's ADC reading
/// is truncated down to 8 bits, still spanning the full 0-255 range); Mono16 keeps the sensor's
/// full native precision in a 16-bit container (see <see cref="ICameraDevice"/>'s mono-only scope
/// note - no colour/Bayer formats, matching this app's mono target cameras).
/// </summary>
public enum CameraOutputFormat
{
    Mono8,
    Mono16,
}

/// <summary>
/// Abstraction over the acquisition camera (ZWO ASI678MM to start with, via SolScan.Infrastructure's
/// native SDK wrapper). Streaming rather than single-shot, since both live preview and SER recording
/// consume the same continuous frame feed - see SolScan CLAUDE.md Phase 4/5.
///
/// Deliberately scoped to streaming/video capture only - no single-shot long-exposure path (NINA's
/// StartExposure/DownloadExposure equivalent). SolScan only ever needs continuous frames (live
/// preview, SER recording); a long-exposure still is a different SDK API family (ASI's
/// StartExposure/GetDataAfterExp vs StartVideoCapture/GetVideoData) that this app has no use for -
/// see CLAUDE.md's "Video vs. long-exposure stills" note under Phase 4.
/// </summary>
public interface ICameraDevice
{
    /// <summary>
    /// Stable identifier for this specific device (e.g. the vendor SDK's camera index/serial) -
    /// used to persist "last used camera" selection and to tell two attached units of the same
    /// model apart. Set at discovery time; doesn't change across connect/disconnect.
    /// </summary>
    string Id { get; }

    /// <summary>Display name for the camera picker (e.g. "ZWO ASI678MM"). Available before
    /// <see cref="ConnectAsync"/> is ever called - the same instance is returned by
    /// <see cref="Camera.ICameraProvider.Discover"/> and then connected, matching N.I.N.A.'s
    /// discover-then-connect shape (see SolScan CLAUDE.md's N.I.N.A. entry).</summary>
    string Name { get; }

    bool IsConnected { get; }
    bool IsStreaming { get; }

    /// <summary>Sensor pixel size in microns, read from the vendor SDK where it exposes one -
    /// available once connected. Null if the vendor SDK doesn't expose it (e.g. <c>AltairCameraDevice</c>,
    /// whose native model-info struct is deliberately left undereferenced - see its own remarks).
    /// Feeds SolScan.App's camera-profile auto-add: the first time a given camera model connects,
    /// CaptureViewModel adds a <see cref="Equipment.CameraProfile"/> for it in
    /// <see cref="Equipment.IEquipmentLibrary"/>, filled in from this rather than left for the user
    /// to type in by hand.</summary>
    double? PixelSizeMicrons { get; }

    /// <summary>Raw sensor gain. Range/units are vendor- (really sensor-) specific - the ASI678MM
    /// (0-600, 0.1dB/step, matching what SharpCap shows for it) is what SolScan.App's sliders are
    /// currently calibrated to; a different sensor's real range may not match.</summary>
    double Gain { get; set; }

    /// <summary>Exposure time in microseconds. SolScan.App's slider maps this on a log scale (see
    /// <see cref="ExposureScale"/>) over the ASI678MM's non-"long exposure mode" range - LX mode
    /// (exposures beyond ~5s) isn't supported, and isn't needed for SHG drift-scan capture anyway.</summary>
    double ExposureMicroseconds { get; set; }

    /// <summary>
    /// USB bandwidth/traffic throttle, 0-100 (ASI's "USB Limit"/"Bandwidth Overload" control).
    /// Streaming-specific: sustained high-fps, full-resolution capture can outrun the USB bus, and
    /// this is the knob that trades throughput headroom against max achievable frame rate. Not a
    /// concern a single long-exposure download ever has - see the class doc comment above.
    /// </summary>
    int UsbBandwidthPercent { get; set; }

    /// <summary>Whether the camera's own auto-gain algorithm is driving <see cref="Gain"/> rather
    /// than the user - mirrors SharpCap's per-slider "Auto" button. A vendor with no independent
    /// auto-gain control (Altair conflates gain and exposure under one auto-exposure flag - see
    /// <c>AltairCameraDevice</c>) may tie this to the same underlying toggle as
    /// <see cref="IsExposureAuto"/>.</summary>
    bool IsGainAuto { get; set; }

    /// <summary>Whether the camera's own auto-exposure algorithm is driving
    /// <see cref="ExposureMicroseconds"/> rather than the user.</summary>
    bool IsExposureAuto { get; set; }

    /// <summary>Whether the camera should pick <see cref="UsbBandwidthPercent"/> itself. Not every
    /// vendor's SDK supports this (Altair's transfer-speed control has no auto mode) - such a
    /// device just ignores the setter and always reports false.</summary>
    bool IsUsbBandwidthAuto { get; set; }

    /// <summary>Current output pixel format - see <see cref="CameraOutputFormat"/>. Change it via
    /// <see cref="SetOutputFormatAsync"/>, not a plain setter: reconfiguring it is a real native
    /// operation (it changes <see cref="CameraFrame"/>'s BitDepth, and on some SDKs needs streaming
    /// briefly stopped and restarted around it), not a cheap value push like <see cref="Gain"/>.</summary>
    CameraOutputFormat OutputFormat { get; }

    /// <summary>Current binning factor (1 = none/1x1). Binning sums/averages NxN sensor pixels
    /// into one, so it also shrinks <see cref="CameraFrame"/>'s Width/Height by this factor - e.g.
    /// binning=2 on a 3840x2160 sensor yields 1920x1080 frames. Change it via
    /// <see cref="SetOutputFormatAsync"/>, alongside <see cref="OutputFormat"/> - ASI sets both in
    /// one native call.</summary>
    int Binning { get; }

    /// <summary>The binning factors this specific camera actually supports (e.g. [1, 2, 3, 4] for
    /// the ASI678MM, read from the SDK's own reported capability list where available) - what
    /// SolScan.App's binning dropdown populates itself from, rather than assuming every camera
    /// supports the same range.</summary>
    IReadOnlyList<int> SupportedBinning { get; }

    /// <summary>
    /// Reconfigures <see cref="OutputFormat"/>, <see cref="Binning"/>, and the region of interest
    /// together - ASI's own native call (<c>ASISetROIFormat</c>) sets all of these in one operation,
    /// so there's no point splitting them apart. If already streaming, stops and restarts streaming
    /// around the change so the caller doesn't have to orchestrate that itself. Not safe to call
    /// while a recording is in progress: the change alters frame geometry/bit depth mid-file, which
    /// a SER file's fixed header can't represent - SolScan.App's CaptureViewModel guards against
    /// this at the UI layer.
    ///
    /// <paramref name="roiWidth"/>/<paramref name="roiHeight"/> are in the *same* post-binning pixel
    /// units as the resulting <see cref="CameraFrame"/>'s own Width/Height (i.e. what the frame
    /// would be at this <paramref name="binning"/> with no ROI applied, then optionally narrowed) -
    /// not full-sensor/unbinned units. 0 (the default) means "full frame" on either axis, clamped
    /// and centred on the sensor if larger than it actually is - see
    /// <see cref="FramePreview.ComputeCenteredRoi"/>, which implementations use for this. This is a
    /// genuine hardware reconfiguration, not a post-capture crop: implementations that support it
    /// (see <c>AsiCameraDevice</c>) tell the sensor itself to only read out and transfer the
    /// requested region, directly reducing USB bandwidth and raising achievable frame rate - unlike
    /// a software crop applied after capture, which does neither (confirmed on real ASI678MM
    /// hardware: a software-only crop left live-view fps identical to full-frame capture, while a
    /// real ASISetROIFormat-driven ROI let ASICap sustain ~4x the frame rate at the same settings).
    /// </summary>
    Task SetOutputFormatAsync(CameraOutputFormat outputFormat, int binning, int roiWidth = 0, int roiHeight = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cumulative count of frames the SDK's internal ring buffer dropped because they weren't
    /// pulled out fast enough - a streaming-only failure mode with no equivalent for a single
    /// on-demand exposure download. Rising during a capture session usually means
    /// <see cref="UsbBandwidthPercent"/> is too high for the current fps/ROI/bit-depth combination,
    /// or the consuming thread (SER writer, live preview) is falling behind.
    /// </summary>
    long DroppedFrameCount { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task StartStreamingAsync(CancellationToken cancellationToken = default);
    Task StopStreamingAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised on the capture thread for every frame while streaming - keep handlers cheap.</summary>
    event EventHandler<CameraFrame>? FrameCaptured;
}
