// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/sun/SpectrumFrameAnalyzer.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

using SolScan.Processing.Math;

namespace SolScan.Processing.Shg;

/// <summary>
/// Detects how the studied spectral line's row position drifts/curves across a frame's width (the
/// "smile" caused by the instrument's optics) - a 2nd-order polynomial fit, with sigma-clipped
/// robust refitting and sub-pixel centroid refinement. Method-for-method port of
/// <c>SpectrumFrameAnalyzer</c>'s curve-fitting pipeline; only the disk left/right border detection
/// (<c>detectBorders</c>/<c>findBordersAuto</c>) is not ported - the original algorithm already treats
/// borders as optional (defaulting to the full frame width when absent), so using the full width here
/// is a real, supported fallback mode of the algorithm itself, not an invented shortcut. It's really a
/// disk-edge-detection concern (closer kin to the deferred ellipse-fitting geometry-correction work)
/// than a line-curvature one.
/// </summary>
public sealed class SpectralLineCurvatureDetector
{
    private const int MaxDeviation = 4;
    private const int ContinuumWindow = 16;
    private const int SigmaClippingIterations = 3;
    private const double SigmaClippingKappa = 3.0;
    private const double MinSigma = 0.01;
    private const double CentroidDepthFraction = 0.25;
    private const int MinSamplesForRefinement = 8;

    private readonly record struct Minimum(double Y, float Value);
    private readonly record struct LineSample(double Y, double Weight);
    private readonly record struct RobustFit(QuadraticPolynomial Fit, List<Point2D> SamplePoints);

    /// <param name="averagedFrame">A clean averaged frame - see <see cref="FrameAverager"/> -
    /// row-major <c>[y, x]</c>, rows = spectral/dispersion axis, columns = spatial axis.</param>
    public QuadraticPolynomial Detect(float[,] averagedFrame)
    {
        var width = averagedFrame.GetLength(1);
        const int left = 0;
        var right = width; // no border detection - see class doc comment
        var mid = (left + right) / 2;

        // Seed from the two darkest local minima at the middle column, build a full fit for each,
        // keep whichever has the lower average sampled value along its curve.
        var seed1 = FindLocalMinimum(mid, averagedFrame, 0);
        var seed2 = FindLocalMinimum(mid, averagedFrame, 1);
        var p1 = FindPolynomialAround(averagedFrame, seed1, mid, left, right);
        var p2 = FindPolynomialAround(averagedFrame, seed2, mid, left, right);
        var avg1 = AverageFor(p1, averagedFrame, left, right);
        var avg2 = AverageFor(p2, averagedFrame, left, right);
        return avg1 < avg2 ? p1 : p2;
    }

    private static QuadraticPolynomial FindPolynomialAround(float[,] data, double centerY, int mid, int left, int right)
    {
        var samplePoints = new List<Point2D>();
        var previousY = -1d;
        if (centerY > 0)
        {
            samplePoints.Add(new Point2D(mid, centerY));
            previousY = centerY;
        }

        for (var x = mid - 1; x >= left; x--)
        {
            var y = FindLocalMinimumClosestTo(x, data, previousY);
            if (y > 0)
            {
                samplePoints.Add(new Point2D(x, y));
                previousY = y;
            }
        }

        previousY = centerY;
        for (var x = mid + 1; x < right; x++)
        {
            var y = FindLocalMinimumClosestTo(x, data, previousY);
            if (y > 0)
            {
                samplePoints.Add(new Point2D(x, y));
                previousY = y;
            }
        }

        var regression = LinearRegression.SecondOrderRegression(samplePoints);
        if (IsFinite(regression) && samplePoints.Count >= MinSamplesForRefinement)
        {
            var refined = RefineFit(regression, data, left, right);
            if (refined is { } robustFit)
            {
                regression = robustFit.Fit;
            }
        }

        return regression;
    }

    /// <summary>Samples <paramref name="data"/> along <paramref name="polynomial"/>'s curve, skipping
    /// any column whose predicted row falls outside the frame - used to pick between the two
    /// candidate fits in <see cref="Detect"/>. Returns <see cref="double.MaxValue"/> if no column
    /// sampled successfully, so a degenerate fit never wins that comparison.</summary>
    private static double AverageFor(QuadraticPolynomial polynomial, float[,] data, int left, int right)
    {
        var height = data.GetLength(0);
        double sum = 0;
        var count = 0;
        for (var x = left; x < right; x++)
        {
            var y = (int)System.Math.Round(polynomial.Evaluate(x));
            if (y < 0 || y >= height)
            {
                continue;
            }

            sum += data[y, x];
            count++;
        }

        return count > 0 ? sum / count : double.MaxValue;
    }

    private static RobustFit? RefineFit(QuadraticPolynomial initialFit, float[,] data, int left, int right)
    {
        var height = data.GetLength(0);
        var points = new List<Point2D>();
        var weights = new List<double>();
        for (var x = left; x < right; x++)
        {
            var predicted = initialFit.Evaluate(x);
            if (predicted < 0 || predicted >= height)
            {
                continue;
            }

            var candidate = DeepestMinimumNear(x, data, predicted);
            if (candidate is not { } minimum)
            {
                continue;
            }

            var sample = DepthWeightedCenter(x, data, minimum);
            if (sample.Weight > 0)
            {
                points.Add(new Point2D(x, sample.Y));
                weights.Add(sample.Weight);
            }
        }

        return points.Count < MinSamplesForRefinement ? null : SigmaClippedFit(points, weights);
    }

    private static LineSample DepthWeightedCenter(int column, float[,] data, Minimum minimum)
    {
        var height = data.GetLength(0);
        var yMin = System.Math.Clamp((int)System.Math.Round(minimum.Y), 0, height - 1);
        var lo = System.Math.Max(0, yMin - ContinuumWindow);
        var hi = System.Math.Min(height - 1, yMin + ContinuumWindow);

        double continuum = 0;
        for (var y = lo; y <= hi; y++)
        {
            continuum = System.Math.Max(continuum, data[y, column]);
        }

        var depth = continuum - minimum.Value;
        if (depth <= 0)
        {
            return new LineSample(minimum.Y, 0);
        }

        var threshold = minimum.Value + (CentroidDepthFraction * depth);
        var runLo = yMin;
        while (runLo > lo && data[runLo - 1, column] <= threshold)
        {
            runLo--;
        }

        var runHi = yMin;
        while (runHi < hi && data[runHi + 1, column] <= threshold)
        {
            runHi++;
        }

        if (runHi - runLo < 2)
        {
            return new LineSample(minimum.Y, depth);
        }

        double sumW = 0;
        double sumWY = 0;
        for (var y = runLo; y <= runHi; y++)
        {
            var w = threshold - data[y, column];
            if (w > 0)
            {
                sumW += w;
                sumWY += w * y;
            }
        }

        return sumW > 0 ? new LineSample(sumWY / sumW, depth) : new LineSample(minimum.Y, depth);
    }

    private static RobustFit? SigmaClippedFit(List<Point2D> points, List<double> weights)
    {
        var currentPoints = points;
        var currentWeights = weights;
        var fit = LinearRegression.SecondOrderRegression(currentPoints, currentWeights);
        if (!IsFinite(fit))
        {
            return null;
        }

        for (var iteration = 0; iteration < SigmaClippingIterations; iteration++)
        {
            var residuals = currentPoints.Select(p => p.Y - fit.Evaluate(p.X)).ToList();
            var center = Median(residuals);
            var absoluteDeviations = residuals.Select(r => System.Math.Abs(r - center)).ToList();
            var sigma = 1.4826 * Median(absoluteDeviations);
            if (sigma < MinSigma)
            {
                break;
            }

            var cutoff = SigmaClippingKappa * sigma;
            var keptPoints = new List<Point2D>();
            var keptWeights = new List<double>();
            for (var i = 0; i < residuals.Count; i++)
            {
                if (System.Math.Abs(residuals[i] - center) <= cutoff)
                {
                    keptPoints.Add(currentPoints[i]);
                    keptWeights.Add(currentWeights[i]);
                }
            }

            if (keptPoints.Count == currentPoints.Count || keptPoints.Count < MinSamplesForRefinement)
            {
                break;
            }

            var newFit = LinearRegression.SecondOrderRegression(keptPoints, keptWeights);
            if (!IsFinite(newFit))
            {
                break;
            }

            fit = newFit;
            currentPoints = keptPoints;
            currentWeights = keptWeights;
        }

        return new RobustFit(fit, currentPoints);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var n = sorted.Count;
        if (n == 0)
        {
            return 0;
        }

        return n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2.0;
    }

    private static bool IsFinite(QuadraticPolynomial p) =>
        double.IsFinite(p.A) && double.IsFinite(p.B) && double.IsFinite(p.C);

    /// <summary>The <paramref name="skip"/>-th deepest local minimum in this column (0 = darkest),
    /// restricted to the middle 80% of the frame height. -1 if none found.</summary>
    private static double FindLocalMinimum(int column, float[,] data, int skip)
    {
        var height = data.GetLength(0);
        var margin = System.Math.Max(1, height / 10);
        var sorted = FindMinima(column, data, margin + 1, height - margin - 1)
            .OrderBy(m => m.Value)
            .Skip(skip)
            .ToList();
        return sorted.Count > 0 ? sorted[0].Y : -1d;
    }

    /// <summary>The deepest local minimum in this column within ±<see cref="MaxDeviation"/> px of
    /// <paramref name="targetY"/> (or the globally deepest minimum if <paramref name="targetY"/> is
    /// negative, meaning "no constraint yet"). Null if none found.</summary>
    private static Minimum? DeepestMinimumNear(int column, float[,] data, double targetY)
    {
        var height = data.GetLength(0);
        Minimum? best = null;
        foreach (var m in FindMinima(column, data, 0, height - 1))
        {
            if (targetY >= 0 && System.Math.Abs(m.Y - targetY) > MaxDeviation)
            {
                continue;
            }

            if (best is null || m.Value < best.Value.Value)
            {
                best = m;
            }
        }

        return best;
    }

    private static double FindLocalMinimumClosestTo(int column, float[,] data, double targetY) =>
        DeepestMinimumNear(column, data, targetY)?.Y ?? -1d;

    /// <summary>Walks the column top-to-bottom detecting local minima (including flat plateaus),
    /// each sub-pixel refined via a parabola fit through its immediate neighbours.</summary>
    private static List<Minimum> FindMinima(int column, float[,] data, int from, int to)
    {
        var minima = new List<Minimum>();
        var height = data.GetLength(0);
        var y = from;
        while (y < to)
        {
            var v = data[y, column];
            var start = y;
            while (y + 1 < height && data[y + 1, column] == v)
            {
                y++;
            }

            var end = y;
            if (start > 0 && end < height - 1)
            {
                var prev = data[start - 1, column];
                var next = data[end + 1, column];
                if (v < prev && v < next)
                {
                    minima.Add(new Minimum(RefineMinimumPosition(start, end, v, prev, next), v));
                }
            }

            y++;
        }

        return minima;
    }

    private static double RefineMinimumPosition(int start, int end, float v, float prev, float next)
    {
        if (start < end)
        {
            return (start + end) / 2.0;
        }

        var denominator = (double)prev - (2 * (double)v) + next;
        if (denominator <= 0)
        {
            return start;
        }

        return start + System.Math.Clamp(0.5 * (prev - next) / denominator, -0.5, 0.5);
    }
}
