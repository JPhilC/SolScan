using System.Windows.Media;

namespace SolScan.App.ViewModels;

/// <summary>
/// One reference line to draw over a <see cref="ProfilePlotData"/> trace, in *data* coordinates.
/// <see cref="double.NaN"/> for a coordinate means "span the full plot extent on that axis", so a full-height
/// vertical marker is <c>(x, NaN, x, NaN)</c>, a full-width horizontal one <c>(NaN, y, NaN, y)</c>, and a
/// bounded segment (e.g. the half-depth line between a spectral line's two crossings) sets all four.
/// </summary>
public readonly record struct PlotLine(double X1, double Y1, double X2, double Y2, Color Colour, bool Dashed = false)
{
    public static PlotLine Vertical(double x, Color colour, bool dashed = true) => new(x, double.NaN, x, double.NaN, colour, dashed);

    public static PlotLine Horizontal(double y, Color colour, bool dashed = true) => new(double.NaN, y, double.NaN, y, colour, dashed);

    public static PlotLine Segment(double x1, double x2, double y, Color colour) => new(x1, y, x2, y, colour, false);
}

/// <summary>
/// A 1D trace plus reference lines for the focus-aid graph window (<c>ProfilePlot</c>): <see cref="Values"/>
/// are plotted at consecutive x positions starting at <see cref="FirstX"/> (a <see cref="double.NaN"/> value
/// leaves a gap), and <see cref="Lines"/> are the measurement's own reference points so the graph shows what
/// the number was derived from. Immutable and safe to hand between threads; <see cref="Values"/> may be
/// shared with the analyzer that produced it, so it must not be modified.
/// </summary>
public sealed record ProfilePlotData(IReadOnlyList<double> Values, double FirstX, IReadOnlyList<PlotLine> Lines);
