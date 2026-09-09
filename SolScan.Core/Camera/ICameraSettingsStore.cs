namespace SolScan.Core.Camera;

/// <summary>The Capture view's per-camera-model dial-in state - everything a user tunes on
/// CaptureView.xaml (gain/exposure/USB throttle and their Auto flags, colour space/binning,
/// contrast stretch) - remembered so reconnecting the same camera later starts from where it was
/// left, rather than the app's own defaults every time.</summary>
public sealed record CameraSettings(
    double Gain,
    double ExposureMicroseconds,
    int UsbBandwidthPercent,
    bool IsGainAuto,
    bool IsExposureAuto,
    bool IsUsbBandwidthAuto,
    CameraOutputFormat OutputFormat,
    int Binning,
    double ContrastBlackPoint,
    double ContrastWhitePoint,
    bool IsContrastAuto);

/// <summary>
/// Persists <see cref="CameraSettings"/> keyed by <see cref="ICameraDevice.Name"/> (e.g. "ZWO
/// ASI678MM") rather than <see cref="ICameraDevice.Id"/> - Id is a discovery-session-local
/// index/serial, not something worth keying saved preferences on, and "remember settings for this
/// camera model" (what was actually asked for) is a Name-level concept: two physically different
/// ASI678MM units, or the same one on a different USB port next session, should still pick up the
/// same remembered settings.
/// </summary>
public interface ICameraSettingsStore
{
    CameraSettings? Load(string cameraName);
    void Save(string cameraName, CameraSettings settings);
}
