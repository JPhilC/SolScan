using SolScan.Core.Processing;

namespace SolScan.App.ViewModels;

/// <summary>
/// One entry in <see cref="CaptureViewModel.SpectralLineLabels"/> - a named line currently visible in
/// the live preview, already positioned in preview-bitmap pixel space (see <see cref="PreviewBitmapY"/>'s
/// own doc comment for the remaining conversion CaptureView.xaml.cs still needs to do). Built from
/// <c>SolScan.Processing.Spectrum.SpectralOverlayAnalyzer</c>'s own <c>VisibleSpectralLine</c> +
/// <c>SpectralOverlayResult.Identification</c> inside <see cref="CaptureViewModel"/>'s throttled
/// preview-processing step, not constructed anywhere in the View itself.
/// </summary>
/// <param name="Ray">The named line.</param>
/// <param name="PreviewBitmapY">Its on-screen row, already converted from the raw frame's own row
/// (<c>VisibleSpectralLine.RowInFrame</c>) into the downsampled preview bitmap's own pixel space via
/// <see cref="SolScan.Core.Camera.FramePreview.ComputeDownsampleScale"/> - CaptureView.xaml.cs's own
/// <c>MapPreviewBitmapYToControlY</c> is the one remaining step (bitmap space → actual on-screen
/// control space, via the live zoom scale), which needs the View's own live layout state and so can't
/// happen here.</param>
/// <param name="IsConfident">Whether this line is <c>SpectralOverlayResult.Identification.IdentifiedRay</c>
/// itself (a confident lock) rather than just the best guess among an unconfident scoring - drives
/// whether the label renders full-opacity or dimmed.</param>
public sealed record SpectralLineLabel(SpectralRay Ray, double PreviewBitmapY, bool IsConfident);
