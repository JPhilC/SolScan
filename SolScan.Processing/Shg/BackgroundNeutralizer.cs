// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/sun/BackgroundRemoval.java
// (`blindBackgroundNeutralization`/`removeZeroPixels`/`backgroundModel`, and
// stretching/AutohistogramStrategy.java's `neutralizeBg` wrapper around it - Apache License,
// Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE file for full
// attribution. `BlindNeutralize` is trimmed to the `ellipse == null` code path - the only one
// DiskEdgeDetector's first-fit (there's no ellipse yet to know about) ever exercises.
// `NeutralizeMasked` (added for AutoStretchStrategy) is a from-scratch reimplementation of
// `backgroundModel`'s degree-2 case + `neutralizeBg` combined - degree hardcoded to 2 (the only value
// either of `neutralizeBg`'s two real call sites in AutohistogramStrategy ever pass), and the
// subtraction-exclusion ellipse parameter dropped (both call sites always pass `null` for it - see
// AutoStretchStrategy's own header comment) - both use SolScan.Processing.Math.LinearSystem's plain
// Gaussian elimination, with a small ridge-regularization term matching the original's own `PENALTY`,
// in place of Apache Commons Math's OLSMultipleLinearRegression/LUDecomposition.

using SolScan.Processing.Math;

namespace SolScan.Processing.Shg;

/// <summary>
/// Removes a low-order background gradient (vignetting, reflections, sky glow) from an image by
/// fitting a 2nd-order polynomial (<c>value ~ c0 + c1*x + c2*y + c3*x^2 + c4*y^2 + c5*x*y</c>) to
/// pixels sampled below an estimated background level on a coarse grid, then subtracting it - "blind"
/// because it has no prior knowledge of where the solar disk sits.
/// </summary>
public static class BackgroundNeutralizer
{
    public readonly record struct Result(float[,] Neutralized, double AverageBackground);

    /// <summary>Retries with progressively coarser histograms (64 bins up to 1024) if the fit looks
    /// unreliable (see the <c>avgBackground &gt; 8*background</c> guard below) - matches astro4j's own
    /// retry loop. Falls back to the (zero-pixel-cleaned) image unchanged, with a background of 0, if
    /// every attempt fails.</summary>
    public static Result BlindNeutralize(float[,] image, double maxPixelValue)
    {
        var bins = 64;
        var result = TryNeutralize(image, bins, maxPixelValue);
        while (result is null && bins < 1024)
        {
            bins *= 2;
            result = TryNeutralize(image, bins, maxPixelValue);
        }

        return result ?? new Result(RemoveZeroPixels(image), 0);
    }

    private static Result? TryNeutralize(float[,] image, int bins, double maxPixelValue)
    {
        var data = RemoveZeroPixels(image);
        var height = data.GetLength(0);
        var width = data.GetLength(1);
        var background = 0.8 * ImageStatistics.EstimateBackgroundLevel(data, bins, maxPixelValue);

        // x/y are normalized to [0,1] before building/evaluating the model. Astro4j's own version
        // gets away with raw pixel coordinates because Apache Commons Math's OLSMultipleLinearRegression
        // solves via QR decomposition, not normal equations - normal equations square whatever
        // condition number the design matrix already has, and for a several-thousand-pixel-wide real
        // capture the unnormalized x^2/y^2/x*y terms make that number astronomical. Confirmed on a real
        // ~2000x1000 capture: without normalizing, LinearSystem.Solve's plain Gaussian elimination
        // silently returned a garbage-coefficient "fit" that zeroed out large parts of the image
        // instead of throwing - Gaussian elimination has no way to notice a matrix is merely nearly (as
        // opposed to exactly) singular. Normalizing keeps every term within O(1), which keeps the
        // normal-equations matrix well-conditioned for a plain elimination solve.
        var widthNorm = (double)System.Math.Max(1, width - 1);
        var heightNorm = (double)System.Math.Max(1, height - 1);

        var samples = new List<(double X, double Y, double Value)>();
        var iterations = 10;
        while (samples.Count < 16 && iterations-- > 0)
        {
            for (var y = 0; y < height; y += 8)
            {
                for (var x = 0; x < width; x += 8)
                {
                    var value = data[y, x];
                    if (value < background && value > 0)
                    {
                        samples.Add((x / widthNorm, y / heightNorm, value));
                    }
                }
            }

            background *= 1.2;
        }

        if (samples.Count < 16)
        {
            return new Result(data, background);
        }

        var coefficients = FitBackgroundModel(samples);
        if (coefficients is null)
        {
            return null;
        }

        double backgroundSum = 0;
        for (var y = 0; y < height; y++)
        {
            var yNorm = y / heightNorm;
            for (var x = 0; x < width; x++)
            {
                var xNorm = x / widthNorm;
                var estimated = coefficients[0] + (coefficients[1] * xNorm) + (coefficients[2] * yNorm)
                    + (coefficients[3] * xNorm * xNorm) + (coefficients[4] * yNorm * yNorm) + (coefficients[5] * xNorm * yNorm);
                backgroundSum += estimated;
                data[y, x] = (float)System.Math.Max(0, data[y, x] - estimated);
            }
        }

        var averageBackground = backgroundSum / (width * (double)height);
        return averageBackground > 8 * background ? null : new Result(data, averageBackground);
    }

    /// <summary>Ordinary least squares for <c>value ~ c0 + c1*x + c2*y + c3*x^2 + c4*y^2 + c5*x*y</c>,
    /// via the normal equations (accumulated directly as sums of term products, same "no generic
    /// design-matrix type" style as <see cref="EllipseRegression"/>) solved by
    /// <see cref="LinearSystem.Solve"/>. <paramref name="ridgePenalty"/> is added to the normal matrix's
    /// diagonal before solving - 0 for <see cref="BlindNeutralize"/>'s own fit, matching astro4j's own
    /// <c>OLSMultipleLinearRegression</c> call (no regularization there); a small positive value for
    /// <see cref="NeutralizeMasked"/>'s, matching astro4j's own <c>backgroundModel</c> (which always
    /// regularizes, <c>PENALTY = 1e-6</c>).</summary>
    private static double[]? FitBackgroundModel(List<(double X, double Y, double Value)> samples, double ridgePenalty = 0)
    {
        var normalMatrix = new double[6, 6];
        var normalVector = new double[6];
        foreach (var (x, y, value) in samples)
        {
            double[] terms = [1, x, y, x * x, y * y, x * y];
            for (var i = 0; i < 6; i++)
            {
                normalVector[i] += terms[i] * value;
                for (var j = 0; j < 6; j++)
                {
                    normalMatrix[i, j] += terms[i] * terms[j];
                }
            }
        }

        if (ridgePenalty != 0)
        {
            for (var i = 0; i < 6; i++)
            {
                normalMatrix[i, i] += ridgePenalty;
            }
        }

        return LinearSystem.Solve(normalMatrix, normalVector);
    }

    /// <summary>Fits a 2nd-order polynomial background model to pixels outside
    /// <paramref name="excludeFromSampling"/> (the whole image, if null), with sigma-clipped outlier
    /// rejection on the sampled values, then subtracts <paramref name="smoothing"/> times that model
    /// from every pixel (including inside the exclusion ellipse - see this type's own header comment
    /// for why that matches the original's real behaviour rather than an oversight). Used by
    /// <c>AutoStretchStrategy</c>'s iterative background-neutralization pass, which knows where the
    /// disk is (unlike <see cref="BlindNeutralize"/>'s "no ellipse yet" situation) and wants to remove
    /// only a fraction of the modeled background per call, not all of it in one shot.</summary>
    /// <returns>The fitted model's average value over the whole image, or 0 if too few samples survived
    /// sigma-clipping to fit at all.</returns>
    public static double NeutralizeMasked(float[,] image, Ellipse? excludeFromSampling, double sigma, float smoothing, double maxPixelValue, float[,] backgroundBuffer)
    {
        var height = image.GetLength(0);
        var width = image.GetLength(1);
        var widthNorm = (double)System.Math.Max(1, width - 1);
        var heightNorm = (double)System.Math.Max(1, height - 1);
        var step = System.Math.Max(1, System.Math.Max(width, height) / 32);

        var samples = new List<(double X, double Y, double Value)>();
        for (var y = 0; y < height; y += step)
        {
            for (var x = 0; x < width; x += step)
            {
                if (excludeFromSampling is { } ellipse && ellipse.IsWithin(x, y))
                {
                    continue;
                }

                samples.Add((x / widthNorm, y / heightNorm, image[y, x]));
            }
        }

        if (samples.Count == 0)
        {
            return 0;
        }

        var mean = 0.0;
        foreach (var s in samples)
        {
            mean += s.Value;
        }

        mean /= samples.Count;
        var varianceSum = 0.0;
        foreach (var s in samples)
        {
            varianceSum += (s.Value - mean) * (s.Value - mean);
        }

        var stddev = System.Math.Sqrt(varianceSum / System.Math.Max(1, samples.Count - 1));
        var hiThreshold = mean + (stddev * sigma);
        var loThreshold = mean - (stddev * sigma);

        const int numTerms = 6;
        var filtered = new List<(double X, double Y, double Value)>(samples.Count);
        foreach (var s in samples)
        {
            if (s.Value > 0 && s.Value < hiThreshold && s.Value > loThreshold)
            {
                filtered.Add(s);
            }
        }

        if (filtered.Count < numTerms)
        {
            return 0;
        }

        var coefficients = FitBackgroundModel(filtered, ridgePenalty: 1e-6);
        if (coefficients is null)
        {
            return 0;
        }

        double backgroundSum = 0;
        for (var y = 0; y < height; y++)
        {
            var yNorm = y / heightNorm;
            for (var x = 0; x < width; x++)
            {
                var xNorm = x / widthNorm;
                var estimated = coefficients[0] + (coefficients[1] * xNorm) + (coefficients[2] * yNorm)
                    + (coefficients[3] * xNorm * xNorm) + (coefficients[4] * yNorm * yNorm) + (coefficients[5] * xNorm * yNorm);
                estimated = System.Math.Clamp(estimated, 0, maxPixelValue);
                backgroundBuffer[y, x] = (float)estimated;
                backgroundSum += estimated;
                image[y, x] = (float)System.Math.Clamp(image[y, x] - (smoothing * estimated), 0, maxPixelValue);
            }
        }

        return backgroundSum / (width * (double)height);
    }

    /// <summary>Replaces zero-valued pixels with the image's own minimum non-zero value, to avoid
    /// artifacts from true zeros (e.g. at a frame's untouched edges) during resizing/regression.</summary>
    private static float[,] RemoveZeroPixels(float[,] image)
    {
        var height = image.GetLength(0);
        var width = image.GetLength(1);
        var minValue = float.MaxValue;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = image[y, x];
                if (v >= 1 && v < minValue)
                {
                    minValue = v;
                }
            }
        }

        var output = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = image[y, x];
                output[y, x] = v == 0 ? minValue : v;
            }
        }

        return output;
    }
}
