namespace SolScan.Processing.Math;

/// <summary>A fitted <c>y = A*x^2 + B*x + C</c> curve - the shape <see cref="LinearRegression.SecondOrderRegression(System.Collections.Generic.IReadOnlyList{Point2D})"/>
/// produces, and what the spectral-line-curvature detector fits to describe how the studied line's
/// row position drifts across a frame's width. Mirrors the evaluate-at-x shape of astro4j's own
/// <c>DoubleTriplet.asPolynomial()</c> (a plain coefficient closure, not a dedicated polynomial
/// class there either).</summary>
public readonly record struct QuadraticPolynomial(double A, double B, double C)
{
    public double Evaluate(double x) => A * x * x + B * x + C;
}
