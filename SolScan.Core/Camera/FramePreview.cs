namespace SolScan.Core.Camera;

/// <summary>
/// Pure helpers turning a raw <see cref="CameraFrame"/> into what a live preview needs - a
/// display-only contrast stretch and a histogram (SharpCap-style: lets the user see if they risk
/// over-exposing before it shows up in a recording). Kept independent of WPF/any UI toolkit so
/// it's unit-testable on its own (see SolScan.Tests) - SolScan.App's CaptureViewModel is what wires
/// the output into an actual bitmap.
///
/// Both <see cref="ComputeHistogram"/> and <see cref="Stretch"/> sample a downsampled
/// (nearest-neighbour strided) grid of the frame, not every pixel - on the ASI678MM's full
/// 3840x2160 sensor, a live preview redrawn ~20x/sec has no business scanning 8.3 million pixels
/// every time just to end up displayed in a window nowhere near that size (ASICap/SharpCap don't
/// either - this is exactly why the live view could sustain ~47fps in ASICap but was lagging
/// badly here even after moving the work off the capture thread, per CLAUDE.md). Recording
/// (<c>SerWriter.WriteFrame</c>) is never touched by this - only the preview is downsampled, the
/// full frame is always what gets written to disk. Written as plain indexed loops rather than a
/// shared <c>IEnumerable&lt;double&gt;</c> iterator too - even downsampled, a `yield return`-based
/// iterator's per-element overhead isn't free at this call frequency.
/// </summary>
/// <summary>See <see cref="FramePreview.ComputeHistogramStats"/>. MinValue/MaxValue/AverageValue
/// are raw sensor values (not normalized 0-1) - e.g. for a 12-bit frame, in [0, 4095].</summary>
public readonly record struct HistogramStats(int[] Histogram, int MinValue, int MaxValue, double AverageValue, int BitDepth);

public static class FramePreview
{
    public const int HistogramBucketCount = 256;

    /// <summary>Preview output is capped to roughly this many pixels on its longest side by
    /// default - comfortably above any realistic display size for the bordered preview panel, far
    /// below a multi-megapixel sensor's native resolution.</summary>
    public const int DefaultMaxPreviewDimension = 960;

    /// <summary>
    /// Applied on top of the linear black/white stretch in <see cref="Stretch"/>. Left at 1
    /// (a no-op) by default - matching ASICap/SharpCap's own default "Display Gamma" of 1.0 -
    /// since the live view's job while dialing in real capture settings is to faithfully show what
    /// will actually be recorded, not an artificially brightened approximation of it. Kept as a
    /// named, adjustable constant rather than removed outright in case a future "prettier preview"
    /// toggle wants to raise it.
    /// </summary>
    private const double DisplayGamma = 1.0;

    /// <summary>Buckets a downsampled sample of <paramref name="frame"/> into a fixed-size
    /// histogram, normalized to the frame's own bit depth so 8-bit and 16-bit frames are directly
    /// comparable.</summary>
    public static int[] ComputeHistogram(CameraFrame frame, int maxDimension = DefaultMaxPreviewDimension)
    {
        var (scale, outWidth, outHeight) = ComputeDownsampleGrid(frame, maxDimension);
        var maxValue = (1 << frame.BitDepth) - 1;
        var bytesPerPixel = frame.BitDepth > 8 ? 2 : 1;
        var rowStride = frame.Width * bytesPerPixel;
        var data = frame.Data;
        var histogram = new int[HistogramBucketCount];

        for (var oy = 0; oy < outHeight; oy++)
        {
            var rowOffset = oy * scale * rowStride;
            for (var ox = 0; ox < outWidth; ox++)
            {
                var index = rowOffset + (ox * scale * bytesPerPixel);
                var value = bytesPerPixel == 2 ? data[index] | (data[index + 1] << 8) : data[index];
                histogram[BucketFor(value, maxValue)]++;
            }
        }

        return histogram;
    }

    /// <summary>
    /// <see cref="ComputeHistogram"/> plus the raw (not normalized-to-0-1) min/max/average sample
    /// values actually seen and the frame's reported bit depth - the same numbers ASICap's own
    /// histogram panel shows (Max/Min/AVG) directly against the sensor's real value range. Exists
    /// so that can be displayed for the user to sanity-check against a reference tool while dialing
    /// in capture settings, and because it's the fastest way to tell whether an unexpected preview
    /// exposure is a display-stretch issue or the underlying reported bit depth/values themselves
    /// being wrong for a given <see cref="CameraOutputFormat"/> - see CLAUDE.md.
    /// </summary>
    public static HistogramStats ComputeHistogramStats(CameraFrame frame, int maxDimension = DefaultMaxPreviewDimension)
    {
        var (scale, outWidth, outHeight) = ComputeDownsampleGrid(frame, maxDimension);
        var maxValue = (1 << frame.BitDepth) - 1;
        var bytesPerPixel = frame.BitDepth > 8 ? 2 : 1;
        var rowStride = frame.Width * bytesPerPixel;
        var data = frame.Data;
        var histogram = new int[HistogramBucketCount];
        var minSeen = int.MaxValue;
        var maxSeen = int.MinValue;
        long sum = 0;
        var count = 0;

        for (var oy = 0; oy < outHeight; oy++)
        {
            var rowOffset = oy * scale * rowStride;
            for (var ox = 0; ox < outWidth; ox++)
            {
                var index = rowOffset + (ox * scale * bytesPerPixel);
                var value = bytesPerPixel == 2 ? data[index] | (data[index + 1] << 8) : data[index];
                histogram[BucketFor(value, maxValue)]++;
                if (value < minSeen)
                {
                    minSeen = value;
                }
                if (value > maxSeen)
                {
                    maxSeen = value;
                }
                sum += value;
                count++;
            }
        }

        return new HistogramStats(
            histogram,
            count == 0 ? 0 : minSeen,
            count == 0 ? 0 : maxSeen,
            count == 0 ? 0 : sum / (double)count,
            frame.BitDepth);
    }

    /// <summary>
    /// Maps [<paramref name="blackPoint"/>, <paramref name="whitePoint"/>] (each 0-1, normalized to
    /// the frame's own bit depth) to full black/white, applies <see cref="DisplayGamma"/> on top,
    /// and returns a downsampled 8-bit-per-pixel buffer plus its actual dimensions (see the class
    /// doc comment) - display-only, never touches what a recording writes to disk.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) Stretch(
        CameraFrame frame, double blackPoint, double whitePoint, int maxDimension = DefaultMaxPreviewDimension)
    {
        var (scale, outWidth, outHeight) = ComputeDownsampleGrid(frame, maxDimension);
        var maxValue = (1 << frame.BitDepth) - 1;
        var bytesPerPixel = frame.BitDepth > 8 ? 2 : 1;
        var rowStride = frame.Width * bytesPerPixel;
        var data = frame.Data;
        var range = Math.Max(whitePoint - blackPoint, 1e-6);
        var invGamma = 1.0 / DisplayGamma;
        var output = new byte[outWidth * outHeight];

        var o = 0;
        for (var oy = 0; oy < outHeight; oy++)
        {
            var rowOffset = oy * scale * rowStride;
            for (var ox = 0; ox < outWidth; ox++, o++)
            {
                var index = rowOffset + (ox * scale * bytesPerPixel);
                var value = bytesPerPixel == 2 ? data[index] | (data[index + 1] << 8) : data[index];
                var sample = value / (double)maxValue;
                output[o] = ToDisplayByte(sample, blackPoint, range, invGamma);
            }
        }

        return (output, outWidth, outHeight);
    }

    /// <summary>
    /// Percentile-based "auto levels" black/white point from a histogram, e.g. SharpCap's "Display
    /// Histogram Stretch" - clips a small fraction of the darkest/brightest pixels rather than
    /// using true min/max (so a handful of hot/dead pixels can't skew the whole result) and stretches
    /// what's left to fill the display range. Off by default in <c>CaptureViewModel</c>
    /// (<c>IsContrastAuto</c>) - useful for just eyeballing a scene, but actively unwanted while
    /// dialing in real Gain/Exposure settings, where the point is to see the actual, unmodified
    /// exposure rather than a software-brightened stand-in for it.
    /// </summary>
    public static (double BlackPoint, double WhitePoint) ComputeAutoStretch(int[] histogram, double clipFraction = 0.001)
    {
        var total = histogram.Sum();
        if (total == 0)
        {
            return (0, 1);
        }

        var lowThreshold = total * clipFraction;
        var highThreshold = total * (1 - clipFraction);

        var cumulative = 0;
        var lowBucket = 0;
        var highBucket = histogram.Length - 1;
        var foundLowBucket = false;

        for (var i = 0; i < histogram.Length; i++)
        {
            cumulative += histogram[i];
            if (!foundLowBucket && cumulative >= lowThreshold)
            {
                lowBucket = i;
                foundLowBucket = true;
            }

            if (cumulative >= highThreshold)
            {
                highBucket = i;
                break;
            }
        }

        var lastBucketIndex = histogram.Length - 1;
        var blackPoint = lowBucket / (double)lastBucketIndex;
        var whitePoint = Math.Max(blackPoint + (1.0 / histogram.Length), highBucket / (double)lastBucketIndex);
        return (blackPoint, whitePoint);
    }

    /// <summary>
    /// Per-bucket bar heights for the histogram graph, in [0,1] against the tallest bucket - but on
    /// a *log* scale (log(count+1) / log(maxCount+1)), not linear.
    ///
    /// SolScan's live view frame is overwhelmingly background/sky pixels around whatever genuinely
    /// interesting content is in it (the slit's bright band, a disk edge starting to clip) - as
    /// exposure rises toward overexposed, the dominant background bucket's pixel count grows faster
    /// than the smaller "interesting" bucket's, so under *linear* normalization the interesting
    /// bucket's relative height keeps shrinking even as its raw count (and <see cref="ComputeHistogramStats"/>'s
    /// own Max/Avg readout) correctly climbs. On CaptureView.xaml's compact 60px-tall histogram
    /// panel, anything under ~1.7% of the peak's count renders under a pixel tall - not small,
    /// genuinely invisible - which is what makes a real, growing "overexposed" hump appear to
    /// flatten out to nothing instead of building on the right as expected. A log scale is the
    /// standard fix for exactly this ("background massively outnumbers signal") shape of problem in
    /// astro-imaging histograms (SharpCap/PixInsight/ASICap all do this): it compresses the *count*
    /// dynamic range so a bucket with even a small fraction of the peak's pixels still gets a
    /// meaningful, visible bar rather than being sub-pixel next to whichever bucket happens to
    /// dominate this particular frame.
    /// </summary>
    public static double[] ComputeHistogramBarHeights(int[] histogram)
    {
        var heights = new double[histogram.Length];
        var maxCount = histogram.Length == 0 ? 0 : histogram.Max();
        if (maxCount <= 0)
        {
            return heights;
        }

        var logMaxCountPlusOne = Math.Log(maxCount + 1);
        for (var i = 0; i < histogram.Length; i++)
        {
            heights[i] = histogram[i] <= 0 ? 0 : Math.Log(histogram[i] + 1) / logMaxCountPlusOne;
        }

        return heights;
    }

    /// <summary>How many source pixels to skip per sampled pixel (in both dimensions) so the
    /// longest side comes out at or under <paramref name="maxDimension"/>, plus the resulting
    /// sampled grid size. Nearest-neighbour (plain striding, no averaging) - simplest and fastest,
    /// and entirely adequate for a live preview/histogram that isn't the recorded data.</summary>
    private static (int Scale, int Width, int Height) ComputeDownsampleGrid(CameraFrame frame, int maxDimension)
    {
        var longestSide = Math.Max(frame.Width, frame.Height);
        var scale = Math.Max(1, (int)Math.Ceiling(longestSide / (double)maxDimension));
        return (scale, Math.Max(1, frame.Width / scale), Math.Max(1, frame.Height / scale));
    }

    /// <summary>
    /// Centres a <paramref name="roiWidth"/> x <paramref name="roiHeight"/> region of interest on a
    /// <paramref name="frameWidth"/> x <paramref name="frameHeight"/> frame, clamped to the frame's
    /// own bounds. A non-positive width/height (the unset default - see
    /// <c>CaptureViewModel.RoiWidth</c>/<c>RoiHeight</c>) means "full frame", so a freshly-connected
    /// camera with no ROI chosen yet behaves exactly as if there were no ROI at all.
    /// </summary>
    public static RoiRect ComputeCenteredRoi(int frameWidth, int frameHeight, int roiWidth, int roiHeight)
    {
        var width = Math.Clamp(roiWidth <= 0 ? frameWidth : roiWidth, 1, frameWidth);
        var height = Math.Clamp(roiHeight <= 0 ? frameHeight : roiHeight, 1, frameHeight);
        var x = (frameWidth - width) / 2;
        var y = (frameHeight - height) / 2;
        return new RoiRect(x, y, width, height);
    }

    /// <summary>
    /// Copies just the <paramref name="roi"/> region out of <paramref name="frame"/> into a new,
    /// smaller <see cref="CameraFrame"/> - what actually gets histogrammed (see
    /// <c>CaptureViewModel</c>'s "histogram driven from the ROI" requirement) and what gets written
    /// to the SER file while recording with a non-full-frame ROI selected. Returns
    /// <paramref name="frame"/> itself, untouched, when <paramref name="roi"/> already covers the
    /// whole frame - the common case, and not worth an extra full-frame copy every call.
    /// </summary>
    public static CameraFrame CropToRoi(CameraFrame frame, RoiRect roi)
    {
        if (roi.X == 0 && roi.Y == 0 && roi.Width == frame.Width && roi.Height == frame.Height)
        {
            return frame;
        }

        var bytesPerPixel = frame.BitDepth > 8 ? 2 : 1;
        var srcStride = frame.Width * bytesPerPixel;
        var dstStride = roi.Width * bytesPerPixel;
        var output = new byte[roi.Width * roi.Height * bytesPerPixel];

        for (var row = 0; row < roi.Height; row++)
        {
            var srcOffset = ((roi.Y + row) * srcStride) + (roi.X * bytesPerPixel);
            var dstOffset = row * dstStride;
            Buffer.BlockCopy(frame.Data, srcOffset, output, dstOffset, dstStride);
        }

        return frame with { Data = output, Width = roi.Width, Height = roi.Height };
    }

    /// <summary>
    /// Maps a full-frame <see cref="RoiRect"/> into the same downsampled preview pixel space
    /// <see cref="Stretch"/>/<see cref="ComputeHistogram"/> render into, so a mask overlay drawn on
    /// top of the (smaller) preview bitmap lines up with the actual ROI rather than the frame's own,
    /// larger pixel coordinates - see <c>CaptureViewModel</c>'s ROI mask.
    /// </summary>
    public static RoiRect ScaleRoiToPreview(RoiRect roi, int frameWidth, int frameHeight, int previewWidth, int previewHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            return roi;
        }

        var x = (int)Math.Round(roi.X * previewWidth / (double)frameWidth);
        var y = (int)Math.Round(roi.Y * previewHeight / (double)frameHeight);
        var width = Math.Max(1, (int)Math.Round(roi.Width * previewWidth / (double)frameWidth));
        var height = Math.Max(1, (int)Math.Round(roi.Height * previewHeight / (double)frameHeight));
        return new RoiRect(x, y, width, height);
    }

    private static int BucketFor(int value, int maxValue) =>
        Math.Min(value * HistogramBucketCount / (maxValue + 1), HistogramBucketCount - 1);

    private static byte ToDisplayByte(double sample, double blackPoint, double range, double invGamma)
    {
        var linear = Math.Clamp((sample - blackPoint) / range, 0, 1);
        var gammaCorrected = invGamma == 1.0 ? linear : Math.Pow(linear, invGamma);
        return (byte)(gammaCorrected * byte.MaxValue);
    }
}
