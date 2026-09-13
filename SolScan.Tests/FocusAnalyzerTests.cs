using SolScan.Core.Camera;

namespace SolScan.Tests;

public class FocusAnalyzerTests
{
    // All synthetic frames here are at least ~70px wide (the algorithm's own minimum - see
    // FocusAnalyzer's width guard) with generous plateaus either side of any transition, so the
    // local low/high reference windows always have room unless a test is deliberately checking the
    // "too close to the border" case.

    private static double[] StepProfile(int width, int transitionStart, int transitionWidth, double lowLevel, double highLevel)
    {
        var values = new double[width];
        for (var x = 0; x < width; x++)
        {
            if (x < transitionStart)
            {
                values[x] = lowLevel;
            }
            else if (x >= transitionStart + transitionWidth)
            {
                values[x] = highLevel;
            }
            else
            {
                var t = (x - transitionStart) / (double)transitionWidth;
                values[x] = lowLevel + (t * (highLevel - lowLevel));
            }
        }
        return values;
    }

    private static double[] HumpProfile(int width, int riseStart, int riseWidth, int plateauWidth, int fallWidth, double lowLevel, double highLevel)
    {
        var fallStart = riseStart + riseWidth + plateauWidth;
        var values = new double[width];
        for (var x = 0; x < width; x++)
        {
            if (x < riseStart)
            {
                values[x] = lowLevel;
            }
            else if (x < riseStart + riseWidth)
            {
                var t = (x - riseStart) / (double)riseWidth;
                values[x] = lowLevel + (t * (highLevel - lowLevel));
            }
            else if (x < fallStart)
            {
                values[x] = highLevel;
            }
            else if (x < fallStart + fallWidth)
            {
                var t = (x - fallStart) / (double)fallWidth;
                values[x] = highLevel - (t * (highLevel - lowLevel));
            }
            else
            {
                values[x] = lowLevel;
            }
        }
        return values;
    }

    /// <summary>Overlays a small, gradual (i.e. much softer than any real edge under test) triangular
    /// dip far from the real transition - a stand-in for the faint, unrelated horizontal band seen in
    /// a real capture, which is what a whole-profile global min/max would get skewed by.</summary>
    private static double[] WithDistantDip(double[] profile, int dipCenter, int dipHalfWidth, double dipFloor)
    {
        var result = (double[])profile.Clone();
        for (var x = Math.Max(0, dipCenter - dipHalfWidth); x <= Math.Min(profile.Length - 1, dipCenter + dipHalfWidth); x++)
        {
            var depthFraction = 1 - (Math.Abs(x - dipCenter) / (double)dipHalfWidth);
            result[x] -= depthFraction * (result[x] - dipFloor);
        }
        return result;
    }

    private static byte[] EncodeRow(double[] values, int bitDepth)
    {
        var bytesPerPixel = bitDepth > 8 ? 2 : 1;
        var row = new byte[values.Length * bytesPerPixel];
        for (var x = 0; x < values.Length; x++)
        {
            var v = (int)Math.Round(values[x]);
            if (bytesPerPixel == 1)
            {
                row[x] = (byte)v;
            }
            else
            {
                row[x * 2] = (byte)(v & 0xFF);
                row[(x * 2) + 1] = (byte)((v >> 8) & 0xFF);
            }
        }
        return row;
    }

    /// <summary>Repeats <paramref name="rowValues"/> for every row of the frame - the row-sampling in
    /// MeasureEdgeSteepness then just reproduces that one row's own profile, making the expected edge
    /// width straightforward to reason about.</summary>
    private static CameraFrame MakeFrame(double[] rowValues, int height, int bitDepth = 8)
    {
        var rowBytes = EncodeRow(rowValues, bitDepth);
        var data = new byte[rowBytes.Length * height];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(rowBytes, 0, data, y * rowBytes.Length, rowBytes.Length);
        }
        return new CameraFrame(data, rowValues.Length, height, bitDepth, DateTime.UtcNow);
    }

    /// <summary>A genuinely 2D synthetic frame (unlike <see cref="MakeFrame"/>, which just repeats one
    /// row down the whole height) with a darker, curved horizontal band - a stand-in for a real SHG
    /// wide view's own spectral absorption line - superimposed on <paramref name="baseProfile"/>'s
    /// x-only signal. <paramref name="lineCenterRow"/> gives the line's own vertical centre at each
    /// column (its "smile" curvature); the band is <paramref name="lineHalfThicknessRows"/> rows thick
    /// either side of that centre and darkens the profile there by <paramref name="lineDepth"/>.</summary>
    private static CameraFrame MakeFrameWithCurvedLine(
        double[] baseProfile, int height, Func<int, double> lineCenterRow, int lineHalfThicknessRows, double lineDepth)
    {
        var width = baseProfile.Length;
        var data = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = baseProfile[x];
                if (Math.Abs(y - lineCenterRow(x)) <= lineHalfThicknessRows)
                {
                    value -= lineDepth;
                }
                data[(y * width) + x] = (byte)Math.Clamp(Math.Round(value), 0, 255);
            }
        }
        return new CameraFrame(data, width, height, 8, DateTime.UtcNow);
    }

    /// <summary>A frame where every row is <paramref name="rowValues"/> plus independent random noise
    /// - a stand-in for a real, very noisy (e.g. high-Gain, overcast-sky) capture.</summary>
    private static CameraFrame MakeNoisyFrame(double[] rowValues, int height, double noiseAmplitude, int seed)
    {
        var width = rowValues.Length;
        var rng = new Random(seed);
        var values = new double[width];
        var data = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var noise = (rng.NextDouble() - 0.5) * 2 * noiseAmplitude;
                values[x] = Math.Clamp(rowValues[x] + noise, 0, 255);
            }
            var rowBytes = EncodeRow(values, 8);
            Array.Copy(rowBytes, 0, data, y * width, width);
        }
        return new CameraFrame(data, width, height, 8, DateTime.UtcNow);
    }

    [Fact]
    public void MeasureEdgeSteepness_ReportsSmallerWidth_ForASharperEdge()
    {
        var sharp = StepProfile(width: 400, transitionStart: 200, transitionWidth: 1, lowLevel: 30, highLevel: 220);
        var soft = StepProfile(width: 400, transitionStart: 200, transitionWidth: 20, lowLevel: 30, highLevel: 220);

        var sharpStats = FocusAnalyzer.MeasureEdgeSteepness(MakeFrame(sharp, height: 4));
        var softStats = FocusAnalyzer.MeasureEdgeSteepness(MakeFrame(soft, height: 4));

        Assert.True(sharpStats.HasEdge);
        Assert.True(softStats.HasEdge);
        Assert.True(sharpStats.EdgeWidthPixels < softStats.EdgeWidthPixels,
            $"Expected the sharp edge ({sharpStats.EdgeWidthPixels}px) to be narrower than the soft one ({softStats.EdgeWidthPixels}px).");
    }

    [Fact]
    public void MeasureEdgeSteepness_DetectsBothEdgesOfADiskCrossing()
    {
        var hump = HumpProfile(width: 700, riseStart: 200, riseWidth: 2, plateauWidth: 100, fallWidth: 2, lowLevel: 20, highLevel: 200);

        var stats = FocusAnalyzer.MeasureEdgeSteepness(MakeFrame(hump, height: 4));

        Assert.True(stats.HasEdge);
        Assert.Equal(2, stats.EdgeCount);
        Assert.True(stats.EdgeWidthPixels > 0);
    }

    [Fact]
    public void MeasureEdgeSteepness_DetectsASingleSlitEdge()
    {
        // Only one real transition in frame (low-to-high, no matching fall back down) - the slit-edge
        // case, distinct from a full disk crossing.
        var step = StepProfile(width: 400, transitionStart: 200, transitionWidth: 1, lowLevel: 20, highLevel: 200);

        var stats = FocusAnalyzer.MeasureEdgeSteepness(MakeFrame(step, height: 4));

        Assert.True(stats.HasEdge);
        Assert.Equal(1, stats.EdgeCount);
        Assert.True(stats.EdgeWidthPixels > 0);
    }

    [Fact]
    public void MeasureEdgeSteepness_ReturnsNoEdge_ForACompletelyFlatFrame()
    {
        var flat = new double[100];
        Array.Fill(flat, 50.0);

        var stats = FocusAnalyzer.MeasureEdgeSteepness(MakeFrame(flat, height: 3));

        Assert.False(stats.HasEdge);
        Assert.Equal(0, stats.EdgeWidthPixels);
        Assert.Equal(0, stats.EdgeCount);
    }

    [Fact]
    public void MeasureEdgeSteepness_ReturnsNoEdge_WhenTheFrameIsNarrowerThanTheMinimumUsableWidth()
    {
        var step = StepProfile(width: 20, transitionStart: 10, transitionWidth: 1, lowLevel: 20, highLevel: 200);

        var stats = FocusAnalyzer.MeasureEdgeSteepness(MakeFrame(step, height: 3));

        Assert.False(stats.HasEdge);
    }

    [Fact]
    public void MeasureEdgeSteepness_ReturnsNoEdge_WhenTheOnlyTransitionIsTooCloseToTheFrameBorder()
    {
        // The rise sits right at the very start of a wide-enough frame - there's no room for the
        // low-side local reference window before it, so this edge can't be measured with confidence.
        var step = StepProfile(width: 300, transitionStart: 5, transitionWidth: 1, lowLevel: 20, highLevel: 200);

        var stats = FocusAnalyzer.MeasureEdgeSteepness(MakeFrame(step, height: 4));

        Assert.False(stats.HasEdge);
    }

    [Fact]
    public void MeasureEdgeSteepness_ReturnsNoEdge_ForATransitionTooGradualToCharacterizeLocally()
    {
        // A transition spread over 200px - far wider than the local reference windows either side can
        // reach past - so the local low/high levels only capture a small slice of the real amplitude
        // rather than the true plateaus. Reporting this as a small (falsely sharp) width would be
        // worse than reporting nothing.
        var veryGradual = StepProfile(width: 500, transitionStart: 150, transitionWidth: 200, lowLevel: 20, highLevel: 200);

        var stats = FocusAnalyzer.MeasureEdgeSteepness(MakeFrame(veryGradual, height: 4));

        Assert.False(stats.HasEdge);
    }

    [Fact]
    public void MeasureEdgeSteepness_IgnoresAnUnrelatedFeatureFarFromTheRealEdge()
    {
        // Regression test for the real-hardware bug this design was reworked to fix: a faint,
        // unrelated feature elsewhere in the frame (here, a soft, distant dip - a stand-in for the
        // faint horizontal band seen in a real capture) must not affect the measurement of a genuine,
        // unrelated edge - a whole-profile global min/max would have been dragged around by it.
        var withoutDip = StepProfile(width: 500, transitionStart: 300, transitionWidth: 1, lowLevel: 30, highLevel: 220);
        var withDip = WithDistantDip(withoutDip, dipCenter: 50, dipHalfWidth: 15, dipFloor: 5);

        var stats = FocusAnalyzer.MeasureEdgeSteepness(MakeFrame(withDip, height: 4));

        Assert.True(stats.HasEdge);
        Assert.Equal(1, stats.EdgeCount); // the dip's own (much softer) rise/fall must be gated out, not reported as a second edge
        Assert.True(stats.EdgeWidthPixels < 10,
            $"Expected the real sharp edge's width (~1px) to still be measured accurately, got {stats.EdgeWidthPixels}px.");
    }

    [Fact]
    public void MeasureEdgeSteepness_IgnoresACurvedSpectralLineRunningThroughTheWholeFrame()
    {
        // Unlike the distant-feature test above, a real SHG wide view *always* shows a darker,
        // slightly curved ("smile") horizontal band - the spectral absorption line itself - running
        // across the *entire* width, including straight through the real edge's own local reference
        // windows, not just somewhere far away from it. The per-column median should still ignore it:
        // at any single column the line only occupies its own local thickness (here 17 of 250 sampled
        // rows, ~7%) - the curvature only changes *which* rows that is per column, not *how many*, so
        // it stays a small minority everywhere and never drags the median.
        var edge = StepProfile(width: 400, transitionStart: 200, transitionWidth: 1, lowLevel: 30, highLevel: 220);
        double LineCenterRow(int x) => 100 + (15 * Math.Sin(x / 400.0 * Math.PI));
        var frame = MakeFrameWithCurvedLine(edge, height: 250, LineCenterRow, lineHalfThicknessRows: 8, lineDepth: 25);

        var stats = FocusAnalyzer.MeasureEdgeSteepness(frame);

        Assert.True(stats.HasEdge);
        Assert.Equal(1, stats.EdgeCount);
        Assert.True(stats.EdgeWidthPixels < 10,
            $"Expected the real edge to still measure sharp despite the spectral line running through it, got {stats.EdgeWidthPixels}px.");
    }

    [Fact]
    public void MeasureEdgeSteepness_StaysBoundedAndConfident_OnAVeryNoisyFrame()
    {
        // Regression test for the reported real-hardware symptom: at a very high Gain (heavy grain),
        // the previous design could report an "edge width" larger than the frame itself. A genuinely
        // sharp edge buried in heavy noise should still measure as small and trustworthy.
        var sharp = StepProfile(width: 400, transitionStart: 200, transitionWidth: 1, lowLevel: 30, highLevel: 220);
        var frame = MakeNoisyFrame(sharp, height: 200, noiseAmplitude: 50, seed: 42);

        var stats = FocusAnalyzer.MeasureEdgeSteepness(frame);

        Assert.True(stats.HasEdge);
        Assert.True(stats.EdgeWidthPixels < 30,
            $"Expected a noisy-but-sharp edge to still measure narrow, got {stats.EdgeWidthPixels}px.");
        Assert.True(stats.EdgeWidthPixels < frame.Width,
            "An edge width can never legitimately exceed the frame's own width.");
    }

    [Fact]
    public void MeasureEdgeSteepness_HandlesSixteenBitLittleEndianSamples()
    {
        var sharp = StepProfile(width: 300, transitionStart: 150, transitionWidth: 1, lowLevel: 1000, highLevel: 60000);

        var stats = FocusAnalyzer.MeasureEdgeSteepness(MakeFrame(sharp, height: 4, bitDepth: 16));

        Assert.True(stats.HasEdge);
        Assert.True(stats.EdgeWidthPixels > 0);
    }

    [Fact]
    public void MeasureEdgeSteepness_HandlesAFrameShorterThanTheDefaultRowSampleCount()
    {
        // A capture ROI far shorter than DefaultProfileRowSampleCount (200) - should still measure
        // cleanly off whatever rows are actually available.
        var sharp = StepProfile(width: 300, transitionStart: 150, transitionWidth: 1, lowLevel: 20, highLevel: 200);

        var stats = FocusAnalyzer.MeasureEdgeSteepness(MakeFrame(sharp, height: 3));

        Assert.True(stats.HasEdge);
        Assert.True(stats.EdgeWidthPixels > 0);
    }

    [Fact]
    public void MeasureEdgeSteepness_MeasuresAWideButGenuineEdge_AtRealSensorScale()
    {
        // Regression test for the reported real-hardware bug: a moderately soft, but perfectly
        // plausible close-to-focus, edge at a realistic full-sensor-scale width (2000px wide, a 150px
        // transition) - far wider than the previous fixed-window design's 35px reach, which rejected
        // exactly this shape of edge as "no edge detected" on real hardware. The adaptive plateau
        // search should find the true low/high levels regardless of how far away they sit.
        var wideButRealEdge = StepProfile(width: 2000, transitionStart: 1000, transitionWidth: 150, lowLevel: 20, highLevel: 200);

        var stats = FocusAnalyzer.MeasureEdgeSteepness(MakeFrame(wideButRealEdge, height: 4));

        Assert.True(stats.HasEdge);
        Assert.Equal(1, stats.EdgeCount);
        // The true 10%-90% span of a linear 150px ramp is 0.8 * 150 = 120px - allow generous slack
        // for the plateau search's own step size rather than pinning an exact figure.
        Assert.InRange(stats.EdgeWidthPixels, 80, 160);
    }
}
