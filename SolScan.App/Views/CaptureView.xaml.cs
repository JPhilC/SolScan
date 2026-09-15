using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SolScan.App.ViewModels;
using SolScan.Core.Processing;

namespace SolScan.App.Views;

/// <summary>
/// Code-behind for the SharpCap-style Zoom dropdown's actual layout math (<see cref="UpdateImageSize"/>)
/// - deliberately not in <see cref="CaptureViewModel"/>: it needs <c>PreviewScrollViewer</c>'s live
/// viewport size, which is a View-layer concern (WPF doesn't make <c>ActualWidth</c>/<c>ActualHeight</c>
/// bindable in a way that avoids this same code living somewhere), not view-model state.
/// </summary>
public partial class CaptureView : UserControl
{
    private static readonly TimeSpan SpectralLabelAnimationDuration = TimeSpan.FromMilliseconds(250);

    private CaptureViewModel? _viewModel;

    /// <summary>One persistent <see cref="TextBlock"/> per currently-shown named line, keyed by
    /// <see cref="SpectralRay"/> - kept across updates (not recreated every throttled tick) so
    /// <see cref="UpdateSpectralLabelOverlay"/> can animate an existing label to its new position
    /// instead of a flicker of remove-then-add, and so a label leaving the visible set can fade out
    /// in place ("roll ... out of view", per the feature request) rather than vanishing instantly.</summary>
    private readonly Dictionary<SpectralRay, TextBlock> _spectralLabelElements = [];

    public CaptureView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PreviewScrollViewer.SizeChanged += (_, _) => UpdateImageSize();
    }

    private void ReticuleOverlay_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateReticuleOverlay();

    private void SpectralLabelOverlay_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSpectralLabelOverlay();

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
        UpdateSpectralLabelOverlay();
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

        // SpectralLineLabels only changes reference on the overlay's own throttled cadence
        // (SpectralOverlayUpdateInterval, not every preview frame) - see CaptureViewModel.
        // ProcessPreviewFrame's own comment - so this isn't a ~20fps cost either.
        if (e.PropertyName is nameof(CaptureViewModel.SpectralLineLabels) or nameof(CaptureViewModel.ShowSpectralLineLabels))
        {
            UpdateSpectralLabelOverlay();
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

    /// <summary>
    /// Rebuilds <see cref="CaptureViewModel.SpectralLineLabels"/> onto <c>SpectralLabelOverlay</c> -
    /// unlike <see cref="UpdateReticuleOverlay"/>, existing <see cref="TextBlock"/>s are kept and
    /// animated to their new position/opacity (see <see cref="_spectralLabelElements"/>'s own doc
    /// comment) rather than cleared and rebuilt every time, since this runs on every throttled
    /// spectral-overlay update (a few times a second - see CaptureViewModel.ProcessPreviewFrame) and
    /// the whole point is a smooth "roll" as a line's position changes or it leaves the visible set,
    /// not a flicker. This app's first use of <see cref="DoubleAnimation"/> - confirmed no existing
    /// precedent elsewhere (see CLAUDE.md) - a plain slide+fade needs nothing beyond WPF's own
    /// built-in animation support.
    /// </summary>
    private void UpdateSpectralLabelOverlay()
    {
        if (_viewModel is not { ShowSpectralLineLabels: true, PreviewBitmap: { } bitmap })
        {
            FadeOutAllSpectralLabels();
            return;
        }

        // This can run right after UpdateImageSize() sets PreviewImage's (and, via the binding in
        // CaptureView.xaml, SpectralLabelOverlay's own) Width/Height to a new value - e.g. a
        // differently-sized PreviewBitmap just arrived in the same OnViewModelPropertyChanged pass
        // that also updates SpectralLineLabels. Setting a layout-affecting property only *schedules* a
        // layout pass; it doesn't run one synchronously, so ActualHeight read immediately afterwards
        // can still reflect the *previous* size - confirmed the hard way against a real loaded test
        // image, where labels landed using a stale (usually larger, since Auto zoom on a smaller image
        // shrinks it) ActualHeight and so didn't line up with the actual image content at all.
        // UpdateLayout() forces that pending pass to complete right now, so ActualHeight below is
        // always current regardless of what just changed.
        SpectralLabelOverlay.UpdateLayout();

        if (SpectralLabelOverlay.ActualHeight <= 0)
        {
            FadeOutAllSpectralLabels();
            return;
        }

        var scale = SpectralLabelOverlay.ActualHeight / bitmap.PixelHeight;
        var currentRays = new HashSet<SpectralRay>();

        foreach (var label in _viewModel.SpectralLineLabels)
        {
            currentRays.Add(label.Ray);
            // "Hovering above" the line per the feature request, not centred on it - offset upward by
            // one label's own rough height so the line's row itself stays visible just below the text.
            // Clamped to stay fully on screen (never negative) - a line detected close to the frame's
            // own top edge has no room above it to hover into anyway, and ClipToBounds="True" on this
            // Canvas would otherwise just cut the label off, which looks identical to it being pinned
            // to the top - confirmed against a real loaded test image where every visible label read as
            // stuck to the top edge.
            var targetTop = Math.Max(0, MapPreviewBitmapYToControlY(label.PreviewBitmapY, scale) - SpectralLabelHoverOffset);
            var targetOpacity = label.IsConfident ? 1.0 : 0.45;

            if (_spectralLabelElements.TryGetValue(label.Ray, out var existing))
            {
                existing.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(targetTop, SpectralLabelAnimationDuration));
                existing.BeginAnimation(OpacityProperty, new DoubleAnimation(targetOpacity, SpectralLabelAnimationDuration));
            }
            else
            {
                var element = CreateSpectralLabelElement(label.Ray);
                Canvas.SetTop(element, targetTop);
                Canvas.SetRight(element, 8);
                element.Opacity = 0; // starts invisible, then fades to targetOpacity below - the "roll in" half of the effect
                SpectralLabelOverlay.Children.Add(element);
                _spectralLabelElements[label.Ray] = element;
                element.BeginAnimation(OpacityProperty, new DoubleAnimation(targetOpacity, SpectralLabelAnimationDuration));
            }
        }

        // Anything currently shown that isn't in this update's visible set any more has "rolled out
        // of view" (or the identifier's own best guess moved on) - fade it out, then remove it once
        // the animation completes rather than vanishing it instantly.
        foreach (var (ray, element) in _spectralLabelElements.Where(kv => !currentRays.Contains(kv.Key)).ToList())
        {
            FadeOutAndRemoveSpectralLabel(ray, element);
        }
    }

    private void FadeOutAllSpectralLabels()
    {
        foreach (var (ray, element) in _spectralLabelElements.ToList())
        {
            FadeOutAndRemoveSpectralLabel(ray, element);
        }
    }

    private void FadeOutAndRemoveSpectralLabel(SpectralRay ray, TextBlock element)
    {
        _spectralLabelElements.Remove(ray);
        var animation = new DoubleAnimation(0, SpectralLabelAnimationDuration);
        animation.Completed += (_, _) => SpectralLabelOverlay.Children.Remove(element);
        element.BeginAnimation(OpacityProperty, animation);
    }

    /// <summary>Converts a label's already-downsampled-to-preview-bitmap-space Y (see
    /// <see cref="SpectralLineLabel"/>'s own doc comment) into an actual on-screen row within
    /// <c>SpectralLabelOverlay</c> - the one remaining step, needing this Canvas's own live
    /// <paramref name="controlToBitmapScale"/> (<c>ActualHeight / PreviewBitmap.PixelHeight</c>),
    /// which only the View can know (mirrors <see cref="UpdateImageSize"/>'s own zoom-scale math -
    /// PreviewImage and SpectralLabelOverlay always render at the identical size, see CaptureView.xaml).</summary>
    private static double MapPreviewBitmapYToControlY(double previewBitmapY, double controlToBitmapScale) =>
        previewBitmapY * controlToBitmapScale;

    /// <summary>Rough on-screen height of one label (font + padding) - see its use in
    /// <see cref="UpdateSpectralLabelOverlay"/> for why this offsets the label upward from its target
    /// row rather than being used for anything pixel-exact.</summary>
    private const double SpectralLabelHoverOffset = 16;

    private static TextBlock CreateSpectralLabelElement(SpectralRay ray) => new()
    {
        Text = ray.Label,
        Foreground = Brushes.White,
        Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
        Padding = new Thickness(4, 1, 4, 1),
        FontSize = 11,
    };

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
}
