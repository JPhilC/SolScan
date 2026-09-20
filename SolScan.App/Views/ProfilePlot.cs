using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SolScan.App.ViewModels;

namespace SolScan.App.Views;

/// <summary>
/// A small, dependency-free line plot for <see cref="ProfilePlotData"/> - the focus-aid graph window's
/// equivalent of sunscan-app's <c>Spectrum.js</c> chart, but also drawing the measurement's own reference
/// lines over the trace. Drawn directly in <see cref="OnRender"/> (same "draw our own geometry" approach the
/// Capture histogram already uses) rather than pulling in a charting library for two simple traces. Colours
/// are fixed light-on-dark: it's only ever hosted in <c>FocusGraphWindow</c>, which is dark.
/// </summary>
public sealed class ProfilePlot : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(ProfilePlotData), typeof(ProfilePlot),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ProfilePlotData? Data
    {
        get => (ProfilePlotData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    private const double LeftMargin = 52;
    private const double RightMargin = 10;
    private const double TopMargin = 8;
    private const double BottomMargin = 22;

    private static readonly Brush TraceBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8)));
    private static readonly Pen FramePen = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), 1));
    private static readonly Typeface Face = new("Segoe UI");

    protected override void OnRender(DrawingContext dc)
    {
        var plotArea = new Rect(LeftMargin, TopMargin, Math.Max(0, ActualWidth - LeftMargin - RightMargin), Math.Max(0, ActualHeight - TopMargin - BottomMargin));
        if (plotArea.Width < 20 || plotArea.Height < 20)
        {
            return;
        }

        dc.DrawRectangle(null, FramePen, plotArea);

        if (Data is not { Values.Count: > 1 } data)
        {
            DrawText(dc, "No data", plotArea.Left + 8, plotArea.Top + 6);
            return;
        }

        // Y range from the finite trace values, padded a little so the trace doesn't touch the frame.
        var yMin = double.MaxValue;
        var yMax = double.MinValue;
        foreach (var v in data.Values)
        {
            if (double.IsFinite(v))
            {
                yMin = Math.Min(yMin, v);
                yMax = Math.Max(yMax, v);
            }
        }

        if (yMin > yMax)
        {
            DrawText(dc, "No data", plotArea.Left + 8, plotArea.Top + 6);
            return;
        }

        var pad = Math.Max((yMax - yMin) * 0.06, 1);
        yMin -= pad;
        yMax += pad;

        var xMin = data.FirstX;
        var xMax = data.FirstX + data.Values.Count - 1;

        double ToX(double x) => plotArea.Left + ((x - xMin) / (xMax - xMin) * plotArea.Width);
        double ToY(double y) => plotArea.Bottom - ((y - yMin) / (yMax - yMin) * plotArea.Height);

        // Clipped so a marker or trace value far outside the visible range can't spill past the frame.
        dc.PushClip(new RectangleGeometry(plotArea));

        foreach (var line in data.Lines)
        {
            var x1 = double.IsNaN(line.X1) ? xMin : line.X1;
            var x2 = double.IsNaN(line.X2) ? xMax : line.X2;
            var y1 = double.IsNaN(line.Y1) ? yMin : line.Y1;
            var y2 = double.IsNaN(line.Y2) ? yMax : line.Y2;
            var pen = new Pen(new SolidColorBrush(line.Colour), line.Dashed ? 1 : 2);
            if (line.Dashed)
            {
                pen.DashStyle = new DashStyle([4, 4], 0);
            }

            dc.DrawLine(pen, new Point(ToX(x1), ToY(y1)), new Point(ToX(x2), ToY(y2)));
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var penDown = false;
            for (var i = 0; i < data.Values.Count; i++)
            {
                var v = data.Values[i];
                if (!double.IsFinite(v))
                {
                    penDown = false;
                    continue;
                }

                var point = new Point(ToX(data.FirstX + i), ToY(v));
                if (penDown)
                {
                    ctx.LineTo(point, true, false);
                }
                else
                {
                    ctx.BeginFigure(point, false, false);
                    penDown = true;
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(TraceBrush, 1.5), geometry);
        dc.Pop();

        // Axis labels: y extremes down the left, x extremes and midpoint along the bottom.
        DrawText(dc, yMax.ToString("0", CultureInfo.CurrentCulture), 4, plotArea.Top - 2);
        DrawText(dc, yMin.ToString("0", CultureInfo.CurrentCulture), 4, plotArea.Bottom - 12);
        DrawText(dc, xMin.ToString("0", CultureInfo.CurrentCulture), plotArea.Left, plotArea.Bottom + 4);
        DrawText(dc, ((xMin + xMax) / 2).ToString("0", CultureInfo.CurrentCulture), plotArea.Left + (plotArea.Width / 2) - 10, plotArea.Bottom + 4);
        DrawText(dc, xMax.ToString("0", CultureInfo.CurrentCulture), plotArea.Right - 24, plotArea.Bottom + 4);
    }

    private void DrawText(DrawingContext dc, string text, double x, double y)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11, TextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(x, y));
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Pen FreezePen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
