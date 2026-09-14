using System.Threading;
using SolScan.Core.Capture;

namespace SolScan.Processing.Capture;

/// <summary>
/// Crops a full-frame .ser capture down to a narrower height, centred on the original frame's
/// vertical centre, at the original width - for recordings made before a hardware ROI was applied
/// (see <see cref="SolScan.Core.Camera.ICameraDevice.SetOutputFormatAsync"/>'s own centred-ROI note),
/// so they can still be trimmed down to roughly what a proper ROI capture would have produced before
/// being handed to <see cref="Shg.IShgProcessor"/>. Unlike a live camera's hardware ROI, this is a
/// plain post-capture software crop - there's no sensor left to reconfigure once a file is already
/// on disk.
/// </summary>
public interface ISerCropper
{
    Task<SerCropResult> CropAsync(
        string sourcePath,
        string outputPath,
        double heightFraction,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <param name="FrameCount">Frames written - always the source file's own <see cref="SerHeader.FrameCount"/>,
/// since cropping never drops a frame, only rows within one.</param>
/// <param name="Width">Unchanged from the source file.</param>
/// <param name="SourceHeight">The source file's own height, for reference.</param>
/// <param name="CroppedHeight">The height actually written - see <see cref="SerCropper.ComputeCroppedHeight"/>
/// for how <c>heightFraction</c> resolves to this.</param>
/// <param name="RowOffset">The first source row kept (0-based) - <see cref="CroppedHeight"/> consecutive
/// rows starting here, centred as closely as integer rounding allows on the source frame's own
/// vertical centre.</param>
public sealed record SerCropResult(int FrameCount, int Width, int SourceHeight, int CroppedHeight, int RowOffset);
