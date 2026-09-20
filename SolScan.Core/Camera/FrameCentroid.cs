namespace SolScan.Core.Camera;

/// <summary>
/// Where a frame's light is concentrated, as fractions of the frame's own width/height (0 = left/top
/// edge, 0.5 = centre, 1 = right/bottom edge). <see cref="HasSignal"/> is false for a flat/dark frame,
/// in which case the centres are meaningless (NaN). <see cref="CentreY"/> can be NaN on its own (a frame
/// with no vertical structure) while <see cref="CentreX"/> is valid - <see cref="HasSignal"/> follows X.
/// </summary>
public readonly record struct FrameCentroidStats(double CentreX, double CentreY, bool HasSignal)
{
    public static FrameCentroidStats None { get; } = new(double.NaN, double.NaN, false);
}

/// <summary>
/// Brightness centroid of a frame, computed from the column-mean and row-mean profiles (each
/// background-subtracted) on a strided sample grid, so it is cheap enough for a live preview frame.
///
/// Built for Find Sun's centring step: in a spectroheliograph frame the Sun only lights the part of
/// the slit its disc crosses, so the *column* profile is bright across the disc's chord and dark
/// outside it - its centroid is where that chord sits along the slit. Brightness alone can't say
/// this (moving the disc along the slit leaves total brightness unchanged - confirmed on real
/// hardware, where RA nudges changed nothing while Dec nudges did), which is why position is
/// measured separately here.
/// </summary>
public static class FrameCentroid
{
    /// <summary>The percentile of a profile taken as its background level.</summary>
    private const double BackgroundPercentile = 0.10;

    /// <summary>A profile must rise at least this fraction of full scale above its background to
    /// count as signal at all - otherwise the "centroid" would just be the centre of the noise.</summary>
    private const double MinSignalFractionOfFullScale = 0.005;

    public static FrameCentroidStats Measure(CameraFrame frame, int stride = 4)
    {
        var bytesPerPixel = frame.BitDepth > 8 ? 2 : 1;
        var width = frame.Width;
        var height = frame.Height;
        stride = Math.Max(1, stride);

        var columnCount = (width + stride - 1) / stride;
        var rowCount = (height + stride - 1) / stride;
        if (columnCount < 2 || rowCount < 2)
            return FrameCentroidStats.None;

        var columnSums = new double[columnCount];
        var rowSums = new double[rowCount];
        var data = frame.Data;
        var rowStride = width * bytesPerPixel;

        for (var r = 0; r < rowCount; r++)
        {
            var rowOffset = r * stride * rowStride;
            double rowSum = 0;
            for (var c = 0; c < columnCount; c++)
            {
                var index = rowOffset + (c * stride * bytesPerPixel);
                double value = bytesPerPixel == 2 ? data[index] | (data[index + 1] << 8) : data[index];
                columnSums[c] += value;
                rowSum += value;
            }
            rowSums[r] = rowSum / columnCount;
        }

        for (var c = 0; c < columnCount; c++)
        {
            columnSums[c] /= rowCount;
        }

        var fullScale = Math.Pow(2, frame.BitDepth) - 1;
        var minRise = fullScale * MinSignalFractionOfFullScale;

        var centreX = ProfileCentroid(columnSums, minRise);
        var centreY = ProfileCentroid(rowSums, minRise);
        // Y is allowed to be NaN on its own (no vertical structure in the frame) - only X drives centring.
        return double.IsNaN(centreX)
            ? FrameCentroidStats.None
            : new FrameCentroidStats(centreX, centreY, true);
    }

    /// <summary>Centroid of <paramref name="profile"/> above its own low-percentile background, as a
    /// 0-1 fraction of its length; NaN if its peak doesn't rise <paramref name="minRise"/> above that.</summary>
    private static double ProfileCentroid(double[] profile, double minRise)
    {
        var sorted = (double[])profile.Clone();
        Array.Sort(sorted);
        var background = sorted[(int)(BackgroundPercentile * (sorted.Length - 1))];
        if (sorted[^1] - background < minRise)
            return double.NaN;

        double weightSum = 0;
        double weightedIndexSum = 0;
        for (var i = 0; i < profile.Length; i++)
        {
            var weight = Math.Max(0, profile[i] - background);
            weightSum += weight;
            weightedIndexSum += weight * i;
        }

        return weightSum <= 0 ? double.NaN : weightedIndexSum / weightSum / (profile.Length - 1);
    }
}
