namespace SolScan.Core.Camera;

/// <summary>
/// A pixel-space region of interest within a full <see cref="CameraFrame"/> - X/Y is the top-left
/// offset from the frame's own origin, Width/Height the ROI's own size. Produced by
/// <see cref="FramePreview.ComputeCenteredRoi"/>, consumed by <see cref="FramePreview.CropToRoi"/>
/// and <see cref="FramePreview.ScaleRoiToPreview"/>.
/// </summary>
public readonly record struct RoiRect(int X, int Y, int Width, int Height);
