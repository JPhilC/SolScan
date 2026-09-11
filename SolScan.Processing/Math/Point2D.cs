namespace SolScan.Processing.Math;

/// <summary>A plain 2D point - used by <see cref="LinearRegression"/> and the spectral-line-curvature
/// detection it backs. Mirrors astro4j's own <c>me.champeau.a4j.math.Point2D</c> shape (just X/Y).</summary>
public readonly record struct Point2D(double X, double Y);
