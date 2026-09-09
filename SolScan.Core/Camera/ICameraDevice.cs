namespace SolScan.Core.Camera;

/// <summary>One captured frame's raw pixel data plus the geometry needed to interpret it.</summary>
public sealed record CameraFrame(byte[] Data, int Width, int Height, int BitDepth, DateTime TimestampUtc);

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
    bool IsConnected { get; }
    bool IsStreaming { get; }

    double Gain { get; set; }
    double ExposureMicroseconds { get; set; }

    /// <summary>
    /// USB bandwidth/traffic throttle, 0-100 (ASI's "USB Limit"/"Bandwidth Overload" control).
    /// Streaming-specific: sustained high-fps, full-resolution capture can outrun the USB bus, and
    /// this is the knob that trades throughput headroom against max achievable frame rate. Not a
    /// concern a single long-exposure download ever has - see the class doc comment above.
    /// </summary>
    int UsbBandwidthPercent { get; set; }

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
