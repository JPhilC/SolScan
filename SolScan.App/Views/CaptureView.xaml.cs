using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SolScan.App.ViewModels;

namespace SolScan.App.Views;

/// <summary>
/// Code-behind for the SharpCap-style Zoom dropdown's actual layout math (<see cref="UpdateImageSize"/>)
/// - deliberately not in <see cref="CaptureViewModel"/>: it needs <c>PreviewScrollViewer</c>'s live
/// viewport size, which is a View-layer concern (WPF doesn't make <c>ActualWidth</c>/<c>ActualHeight</c>
/// bindable in a way that avoids this same code living somewhere), not view-model state.
/// </summary>
public partial class CaptureView : UserControl
{
    private CaptureViewModel? _viewModel;

    public CaptureView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PreviewScrollViewer.SizeChanged += (_, _) => UpdateImageSize();
    }

    private void ReticuleOverlay_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateReticuleOverlay();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as CaptureViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateImageSize();
        UpdateReticuleOverlay();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // PreviewBitmap only actually changes *reference* (triggering this) when its dimensions
        // change - CaptureViewModel.RenderPreview reuses the same WriteableBitmap, just rewriting
        // its pixels, for every frame that doesn't change size - so this isn't a per-frame (~20fps)
        // cost, only a real "the preview's own pixel size changed" or "the user changed Zoom" one.
        if (e.PropertyName is nameof(CaptureViewModel.SelectedZoomOption) or nameof(CaptureViewModel.PreviewBitmap))
        {
            UpdateImageSize();
        }

        if (e.PropertyName is nameof(CaptureViewModel.ShowCrosshairReticule)
            or nameof(CaptureViewModel.ShowRotationReticule)
            or nameof(CaptureViewModel.ReticuleAngleDegrees)
            or nameof(CaptureViewModel.ReticuleInsetPixels))
        {
            UpdateReticuleOverlay();
        }
    }

    /// <summary>
    /// Sets PreviewImage's actual Width/Height from whichever <see cref="ZoomOption"/> is currently
    /// selected, PreviewScrollViewer's live viewport size, and PreviewBitmap's own native pixel
    /// size - Stretch="Fill" on the Image (set in XAML) then does a literal, non-aspect-adjusting
    /// scale to exactly that size, since aspect ratio is already accounted for here.
    ///
    /// A subtlety worth knowing about rather than solving: for FitWidth/FitHeight/Auto, setting the
    /// image to exactly fill one axis of *today's* viewport can itself cause a scrollbar to appear
    /// on the other axis, which shrinks the viewport slightly along the first axis too (a scrollbar
    /// occupies real space) - a classic WPF ScrollViewer sizing quirk with no clean fixed-point
    /// solve. In practice this only shows up as an occasional few-pixel sliver of unwanted scroll
    /// right at a boundary case, not a functional problem, and matches what most zoom-to-fit
    /// implementations (including, going by its screenshots, SharpCap's own) live with.
    /// </summary>
    private void UpdateImageSize()
    {
        if (_viewModel?.PreviewBitmap is not { } bitmap)
        {
            return;
        }

        var viewportWidth = PreviewScrollViewer.ViewportWidth;
        var viewportHeight = PreviewScrollViewer.ViewportHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            // Not laid out yet (e.g. DataContext assigned before the first layout pass) - the
            // SizeChanged handler above will call this again once real dimensions are known.
            return;
        }

        var pixelWidth = (double)bitmap.PixelWidth;
        var pixelHeight = (double)bitmap.PixelHeight;

        double width;
        double height;
        switch (_viewModel.SelectedZoomOption.Kind)
        {
            case ZoomKind.FitWidth:
                width = viewportWidth;
                height = pixelHeight * (viewportWidth / pixelWidth);
                break;
            case ZoomKind.FitHeight:
                height = viewportHeight;
                width = pixelWidth * (viewportHeight / pixelHeight);
                break;
            case ZoomKind.Fixed:
                var scale = _viewModel.SelectedZoomOption.Percent / 100.0;
                width = pixelWidth * scale;
                height = pixelHeight * scale;
                break;
            case ZoomKind.Auto:
            default:
                var autoScale = Math.Min(viewportWidth / pixelWidth, viewportHeight / pixelHeight);
                width = pixelWidth * autoScale;
                height = pixelHeight * autoScale;
                break;
        }

        PreviewImage.Width = width;
        PreviewImage.Height = height;
    }

    /// <summary>
    /// Redraws the reticule overlay (crosshair + rotation guides, see CaptureViewModel's own doc
    /// comments) against ReticuleOverlay's own live size - deliberately *not* PreviewImage's size:
    /// the whole point is that these lines stay a fixed on-screen thickness/position regardless of
    /// <see cref="ZoomOption"/>, so they're drawn directly against the fixed viewport (ReticuleOverlay
    /// sits as a sibling of, on top of, PreviewScrollViewer - see CaptureView.xaml) rather than being
    /// part of the zoomed/scrolled image content. Just clears and rebuilds the Canvas's children each
    /// time rather than updating individual Line objects in place - this only runs on a resize or a
    /// deliberate toggle/angle/inset change, never per preview frame, so the extra allocation is free.
    /// </summary>
    private void UpdateReticuleOverlay()
    {
        ReticuleOverlay.Children.Clear();

        if (_viewModel is null)
        {
            return;
        }

        var width = ReticuleOverlay.ActualWidth;
        var height = ReticuleOverlay.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_viewModel.ShowCrosshairReticule)
        {
            ReticuleOverlay.Children.Add(CreateReticuleLine(width / 2, 0, width / 2, height));
            ReticuleOverlay.Children.Add(CreateReticuleLine(0, height / 2, width, height / 2));
        }

        if (_viewModel.ShowRotationReticule)
        {
            // Clamped so a large inset (relative to a narrow/zoomed-out viewport) can't push the two
            // lines past each other.
            var inset = Math.Clamp(_viewModel.ReticuleInsetPixels, 0, (width / 2) - 1);
            // Mirrored, not equal, angles: an SHG's two slit edges slope toward or away from each
            // other (they aren't parallel the way two independently-tilted lines would be), so the
            // right-hand line pivots by the negative of the left-hand one - together they open into a
            // V or a diverging "outward" shape rather than both leaning the same way.
            ReticuleOverlay.Children.Add(CreatePivotedVerticalLine(inset, height, _viewModel.ReticuleAngleDegrees));
            ReticuleOverlay.Children.Add(CreatePivotedVerticalLine(width - inset, height, -_viewModel.ReticuleAngleDegrees));
        }
    }

    private static Line CreateReticuleLine(double x1, double y1, double x2, double y2) => new()
    {
        X1 = x1,
        Y1 = y1,
        X2 = x2,
        Y2 = y2,
        Stroke = Brushes.Red,
        StrokeThickness = 1,
        SnapsToDevicePixels = true,
    };

    /// <summary>A full-height vertical line at <paramref name="x"/>, pivoted by <paramref name="angleDegrees"/>
    /// about its own midpoint - "pivoting half way along the line" per the feature request, which a
    /// <see cref="RotateTransform"/> centred at (x, height/2) gives directly without needing to compute
    /// rotated endpoints by hand.</summary>
    private static Line CreatePivotedVerticalLine(double x, double height, double angleDegrees)
    {
        var line = CreateReticuleLine(x, 0, x, height);
        line.RenderTransform = new RotateTransform(angleDegrees, x, height / 2);
        return line;
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {

    }
}
