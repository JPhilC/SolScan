namespace SolScan.Core.Camera;

/// <summary>One captured frame's raw pixel data plus the geometry needed to interpret it.</summary>
public sealed record CameraFrame(byte[] Data, int Width, int Height, int BitDepth, DateTime TimestampUtc);

/// <summary>
/// Abstraction over the acquisition camera (ZWO ASI678MM to start with, via SolScan.Infrastructure's
/// native SDK wrapper). Streaming rather than single-shot, since both live preview and SER recording
/// consume the same continuous frame feed - see SolScan CLAUDE.md Phase 4/5.
/// </summary>
public interface ICameraDevice
{
    bool IsConnected { get; }
    bool IsStreaming { get; }

    double Gain { get; set; }
    double ExposureMicroseconds { get; set; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task StartStreamingAsync(CancellationToken cancellationToken = default);
    Task StopStreamingAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised on the capture thread for every frame while streaming - keep handlers cheap.</summary>
    event EventHandler<CameraFrame>? FrameCaptured;
}
