using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
}
