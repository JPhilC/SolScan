namespace SolScan.App.ViewModels;

/// <summary>The three "fit to available space" modes, or a literal fixed percentage - see
/// <see cref="ZoomOption"/>.</summary>
public enum ZoomKind
{
    /// <summary>Fits the whole preview within the available viewport, preserving aspect ratio -
    /// like <c>Stretch="Uniform"</c>, no scrollbars ever needed. SharpCap's own default.</summary>
    Auto,

    /// <summary>Preview width exactly fills the viewport width, height follows to preserve aspect
    /// ratio - may need a vertical scrollbar if that overflows the viewport height.</summary>
    FitWidth,

    /// <summary>Preview height exactly fills the viewport height, width follows to preserve aspect
    /// ratio - may need a horizontal scrollbar if that overflows the viewport width.</summary>
    FitHeight,

    /// <summary>A literal, fixed <see cref="ZoomOption.Percent"/> of the preview bitmap's own
    /// native pixel size, independent of the viewport - scrollbars appear whenever that's bigger
    /// than the available space.</summary>
    Fixed,
}

/// <summary>
/// One entry in the Capture view's SharpCap-style Zoom dropdown (<see cref="CaptureViewModel.AvailableZoomOptions"/>/
/// <see cref="CaptureViewModel.SelectedZoomOption"/>). The actual pixel width/height this resolves
/// to is computed in CaptureView.xaml.cs, not the view model - it needs the live ScrollViewer
/// viewport size, which is a View-layer concern, not view-model state.
/// </summary>
/// <param name="Percent">Only meaningful when <paramref name="Kind"/> is <see cref="ZoomKind.Fixed"/>.</param>
public readonly record struct ZoomOption(string Label, ZoomKind Kind, double Percent = 0)
{
    public override string ToString() => Label;
}
