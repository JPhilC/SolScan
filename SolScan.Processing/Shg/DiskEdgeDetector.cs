// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/sun/tasks/EllipseFittingTask.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.
//
// One deliberate omission: the original squares each pixel's normalized value (`v = data/MAX; data =
// v*v*MAX`, a contrast boost) right after the Gaussian blur, storing the result into `data` - but
// `data` was captured from the *pre-blur* array, and `tmp` (what actually feeds every later step) was
// already reassigned to a freshly-allocated post-blur array by that point. Since `convolve` always
// allocates a new backing array rather than mutating in place, that square step mutates an array nothing
// downstream ever reads again - it's dead code in the original, confirmed by tracing `ImageMath.convolve`
// (`default Image convolve(...)` always builds a new `output` buffer). This port skips it rather than
// reproducing a no-op.

using SolScan.Processing.Math;

namespace SolScan.Processing.Shg;

/// <summary>
/// Fits an ellipse to the solar disk's edge in a reconstructed image: blurs it to smooth over noise,
/// removes background gradient (vignetting/reflections), stretches contrast, scans for the
/// bright/dark threshold crossing along every row and column (interpolated to sub-pixel), filters
/// obviously-wrong samples, and fits an ellipse through what's left - repeating the outlier filter and
/// falling back to a smaller sample set if the fit itself fails.
/// </summary>
public static class DiskEdgeDetector
{
    private const int MinimumSamples = 32;
    private const int BoxBlurSize = 8;
    private const int BackgroundNeutralizationMaxIterations = 16;
    private const double BackgroundNeutralizationConvergence = 0.02;

    /// <returns>The fitted ellipse, or null if the image doesn't contain enough of a clear disk edge
    /// to fit one (too few threshold-crossing samples survived filtering, or every fit attempt on the
    /// samples that did survive threw).</returns>
    public static Ellipse? Detect(float[,] sourceImage, double maxPixelValue)
    {
        var height = sourceImage.GetLength(0);
        var width = sourceImage.GetLength(1);
        if (height == 0 || width == 0)
        {
            return null;
        }

        var working = Prepare(sourceImage, maxPixelValue);
        return FitEllipse(working, width, height, maxPixelValue);
    }

    /// <summary>Blur → pre-stretch → iterative background neutralization → final stretch.</summary>
    private static float[,] Prepare(float[,] sourceImage, double maxPixelValue)
    {
        var working = Truncate((float[,])sourceImage.Clone());
        working = ImageConvolution.Convolve(working, ImageConvolution.GaussianBlur3X3, ImageConvolution.GaussianBlur3X3Factor, maxPixelValue);
        var (boxKernel, boxFactor) = ImageConvolution.BoxKernel(BoxBlurSize);
        working = ImageConvolution.Convolve(working, boxKernel, boxFactor, maxPixelValue);

        // Stretch *before* background neutralization, not just after (astro4j's own equivalent only
        // stretches at the end - see the class header comment for why that's not enough here): the
        // background-level estimate and the per-pixel background model are both grid-independent of
        // scale in theory, but EstimateBackgroundLevel's histogram always bins over the full
        // [0, maxPixelValue] range regardless of what part of it the data actually occupies. A raw
        // reconstruction's native sensor counts can occupy only a small slice of that range (confirmed
        // against a real low-contrast Sunscan capture using well under 10% of the 16-bit range) which
        // starves the histogram of resolution, producing a wildly overestimated background level; the
        // neutralization loop below then keeps subtracting a shrinking-but-still-substantial fraction
        // of the *signal itself* every iteration, geometric-decaying without ever converging within its
        // 2% threshold, and destroys large parts of the image over the full 16-iteration cap.
        LinearStretch(working, maxPixelValue);

        // Background neutralization is iterated until the estimated background level converges
        // (within 2%) or a hard iteration cap is hit - some gradients are particularly stubborn to
        // remove in one pass.
        var neutralized = BackgroundNeutralizer.BlindNeutralize(working, maxPixelValue);
        working = neutralized.Neutralized;
        var previousBackground = maxPixelValue;
        var iterationsLeft = BackgroundNeutralizationMaxIterations;
        while (RelativeChangeExceeds(previousBackground, neutralized.AverageBackground) && iterationsLeft-- > 0)
        {
            previousBackground = neutralized.AverageBackground;
            neutralized = BackgroundNeutralizer.BlindNeutralize(working, maxPixelValue);
            working = neutralized.Neutralized;
        }

        LinearStretch(working, maxPixelValue);
        return working;
    }

    private static bool RelativeChangeExceeds(double a, double b)
    {
        var denominator = System.Math.Max(a, b);
        return denominator != 0 && System.Math.Abs(a - b) / denominator > BackgroundNeutralizationConvergence;
    }

    /// <summary>Replaces zero-valued pixels with the image's own minimum non-zero value - mutates
    /// <paramref name="data"/> in place (the caller already owns a private copy at this point).</summary>
    private static float[,] Truncate(float[,] data)
    {
        var stats = ImageStatistics.ComputeStats(data);
        var height = data.GetLength(0);
        var width = data.GetLength(1);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (data[y, x] == 0)
                {
                    data[y, x] = stats.MinNonZero;
                }
            }
        }

        return data;
    }

    /// <summary>Rescales <paramref name="data"/> in place so its true min/max span exactly
    /// <c>[0, maxPixelValue]</c> - astro4j's <c>LinearStrechingStrategy.DEFAULT</c>.</summary>
    private static void LinearStretch(float[,] data, double maxPixelValue)
    {
        var height = data.GetLength(0);
        var width = data.GetLength(1);
        var min = double.MaxValue;
        var max = double.MinValue;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = data[y, x];
                min = System.Math.Min(min, v);
                max = System.Math.Max(max, v);
            }
        }

        var range = max - min;
        if (range == 0)
        {
            return;
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = (float)(maxPixelValue / range * (data[y, x] - min));
            }
        }
    }

    private static Ellipse? FitEllipse(float[,] magnitudes, int width, int height, double maxPixelValue)
    {
        var samples = Decimate(FindSamplesUsingDynamicSensitivity(magnitudes, width, height, maxPixelValue), width, height);

        var previousSize = -1;
        while (samples.Count != previousSize)
        {
            var previousSamples = new List<Point2D>(samples);
            previousSize = samples.Count;
            if (!FilterOutliersByDistanceToEllipse(samples, 2.5, 5) || samples.Count < MinimumSamples)
            {
                samples = previousSamples;
                break;
            }
        }

        if (samples.Count < MinimumSamples)
        {
            return null;
        }

        while (true)
        {
            try
            {
                return new EllipseRegression(samples).Solve();
            }
            catch (InvalidOperationException)
            {
                if (samples.Count > 2 * MinimumSamples)
                {
                    samples = Decimate(new List<Point2D>(samples), width, height);
                }
                else
                {
                    return null;
                }
            }
        }
    }

    private static List<Point2D> FindSamplesUsingDynamicSensitivity(float[,] magnitudes, int width, int height, double maxPixelValue)
    {
        var stats = ImageStatistics.ComputeStats(magnitudes);
        var maxMagnitude = stats.Max;
        var sensitivity = 0.5 * (stats.Min + stats.StdDev) / maxPixelValue;
        var minLimit = sensitivity * maxMagnitude;

        var samples = new HashSet<Point2D>();
        Scan(samples, minLimit, width, height, magnitudes, scanInYDirection: true);
        Scan(samples, minLimit, width, height, magnitudes, scanInYDirection: false);
        return FilterOutliersByDetectingLines(samples);
    }

    /// <summary>Scans every row (or, in the other direction, every column) for the first
    /// threshold-crossing from each end, sub-pixel refined - two samples per row/column, roughly
    /// tracing the bright disk's left/right (or top/bottom) edge.</summary>
    private static void Scan(HashSet<Point2D> samples, double minLimit, int width, int height, float[,] magnitudes, bool scanInYDirection)
    {
        const int first = 0;
        var last = scanInYDirection ? width : height;
        var outerLimit = scanInYDirection ? height : width;

        for (var i = 0; i < outerLimit; i++)
        {
            double min = -1;
            double max = -1;
            for (var j = first; j < last; j++)
            {
                var x = scanInYDirection ? j : i;
                var y = scanInYDirection ? i : j;
                var mag = magnitudes[y, x];
                if (min < 0 && mag > minLimit)
                {
                    var index = scanInYDirection ? x : y;
                    if (j > first)
                    {
                        var previous = scanInYDirection ? magnitudes[y, x - 1] : magnitudes[y - 1, x];
                        min = InterpolateCrossing(index - 1, previous, index, mag, minLimit);
                    }
                    else
                    {
                        min = index;
                    }
                }

                mag = scanInYDirection ? magnitudes[y, width - x - 1] : magnitudes[height - y - 1, x];
                if (max < 0 && mag > minLimit)
                {
                    var index = scanInYDirection ? width - x - 1 : height - y - 1;
                    if (j > first)
                    {
                        var previous = scanInYDirection ? magnitudes[y, index + 1] : magnitudes[index + 1, x];
                        max = InterpolateCrossing(index + 1, previous, index, mag, minLimit);
                    }
                    else
                    {
                        max = index;
                    }
                }

                if (min >= 0 && max >= 0)
                {
                    break;
                }
            }

            if (min >= 0)
            {
                samples.Add(scanInYDirection ? new Point2D(min, i) : new Point2D(i, min));
            }

            if (max >= 0)
            {
                samples.Add(scanInYDirection ? new Point2D(max, i) : new Point2D(i, max));
            }
        }
    }

    private static double InterpolateCrossing(int previousIndex, float previousMagnitude, int index, float magnitude, double minLimit)
    {
        var delta = magnitude - previousMagnitude;
        if (delta <= 0)
        {
            return index;
        }

        return previousIndex + ((minLimit - previousMagnitude) * (index - previousIndex) / delta);
    }

    /// <summary>Drops samples piled up in a single row or column beyond what a genuine disk edge would
    /// produce - usually a sign a straight artifact (not the disk) got picked up instead. Falls back
    /// to an evenly-strided subsample of the pre-filter set if that leaves too few points to fit,
    /// which happens for genuinely very "flat" disks.</summary>
    private static List<Point2D> FilterOutliersByDetectingLines(HashSet<Point2D> samples)
    {
        var restore = new List<Point2D>(samples);
        var byX = restore.GroupBy(p => (int)p.X).ToDictionary(g => g.Key, g => g.Count());
        var byY = restore.GroupBy(p => (int)p.Y).ToDictionary(g => g.Key, g => g.Count());
        var sX = 8 + System.Math.Sqrt(byX.Count / 2d);
        var sY = 8 + System.Math.Sqrt(byY.Count / 2d);

        var removedX = new HashSet<int>();
        var removedY = new HashSet<int>();
        foreach (var (x, count) in byX)
        {
            if (count > sX)
            {
                for (var i = x - 2; i < x + 2; i++)
                {
                    removedX.Add(i);
                }
            }
        }

        foreach (var (y, count) in byY)
        {
            if (count > sY)
            {
                for (var i = y - 2; i < y + 2; i++)
                {
                    removedY.Add(i);
                }
            }
        }

        var filtered = restore.Where(p => !removedX.Contains((int)p.X) && !removedY.Contains((int)p.Y)).ToList();
        if (filtered.Count < MinimumSamples && restore.Count > 2 * MinimumSamples)
        {
            var size = restore.Count;
            if (size > 512)
            {
                var step = size / 512;
                for (var i = 0; i < size; i += step)
                {
                    filtered.Add(restore[i]);
                }
            }
            else
            {
                filtered.AddRange(restore);
            }
        }

        return filtered;
    }

    /// <summary>Keeps the 80% of points farthest from the image centre, dropping the nearest 20% -
    /// helps when stray bright samples were picked up well inside the disk rather than at its edge.</summary>
    private static List<Point2D> Decimate(List<Point2D> points, int width, int height)
    {
        var cx = width / 2.0;
        var cy = height / 2.0;
        var sorted = points
            .OrderByDescending(p => System.Math.Sqrt(((p.X - cx) * (p.X - cx)) + ((p.Y - cy) * (p.Y - cy))))
            .ToList();
        var keep = (int)(0.8 * points.Count);
        return sorted.Take(keep).ToList();
    }

    /// <summary>Fits a trial ellipse, drops samples too far from it (distance measured along a single
    /// axis, not true Euclidean - a fast approximation adequate for outlier rejection), and reports
    /// whether the trial fit succeeded at all. Mutates <paramref name="samples"/> in place.</summary>
    private static bool FilterOutliersByDistanceToEllipse(List<Point2D> samples, double sigma, double cutoff)
    {
        Ellipse initialEllipse;
        try
        {
            initialEllipse = new EllipseRegression(samples).Solve();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        samples.RemoveAll(p => initialEllipse.FindY(p.X) is null || initialEllipse.FindX(p.Y) is null);

        var avgDistance = samples.Count == 0 ? 10 : samples.Average(p => DistanceToEllipse(p, initialEllipse));
        var sumSquaredDeviation = samples.Sum(p =>
        {
            var d = DistanceToEllipse(p, initialEllipse) - avgDistance;
            return d * d;
        });
        var stddev = System.Math.Sqrt(sumSquaredDeviation / samples.Count); // 0/0 -> NaN when empty, same as the Java original

        if (stddev < cutoff)
        {
            return true;
        }

        var threshold = (sigma * stddev) + avgDistance;
        samples.RemoveAll(p => DistanceToEllipse(p, initialEllipse) > threshold);
        return true;
    }

    private static double DistanceToEllipse(Point2D p, Ellipse ellipse)
    {
        var maybeY = ellipse.FindY(p.X);
        if (maybeY is null)
        {
            var (x1, x2) = ellipse.FindX(p.Y)!.Value;
            return System.Math.Min(System.Math.Abs(p.X - x1), System.Math.Abs(p.X - x2));
        }

        var (y1, y2) = maybeY.Value;
        return System.Math.Min(System.Math.Abs(p.Y - y1), System.Math.Abs(p.Y - y2));
    }
}
