// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/stretching/AutohistogramStrategy.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution. This is what astro4j's own ContrastEnhancement.AUTOSTRETCH mode actually
// runs - a composite of iterative ellipse-aware background neutralization, two differently-tuned CLAHE
// passes (one for the disk, one - "eclipse" in the original's own naming - for the region outside it,
// to bring out prominences without the disk's own brightness dominating the tile statistics), a gamma
// curve, and an asinh brightness-matching stretch anchored to a target disk midtone, blended together
// and finished with a cubic contrast S-curve that fades to identity via the ellipse's own geometry so
// it doesn't crush prominences/the limb. Two deliberate simplifications versus the original, both
// because SolScan.Core.Processing.ContrastEnhancementMode has no tunable AutoStretchParams yet (see its
// own doc comment - "a real pipeline would use sensible built-in defaults for now"):
//   - `adjustBrightness` is always true - the only value astro4j's own single real call site
//     (ProcessingWorkflow.produceStretchedImage) ever constructs this with.
//   - `protusStretch` defaults to 0 (astro4j's own DEFAULT_PROM_STRETCH) - at that value the
//     "expand" curve applied to the eclipse/prominence pass is provably the identity (see
//     ApplyProminenceExpand's own comment), so it's still ported (ready for whenever this becomes a
//     real tunable) rather than assumed away, but the identity case is short-circuited to skip the
//     redundant work.
// The original's OpenCL/GPU paths (inherited transitively via GammaStrategy/ClaheStrategy) are dropped
// throughout, same as those types' own header comments.

using SolScan.Processing.Math;
using SolScan.Processing.Shg;

namespace SolScan.Processing.Stretching;

/// <summary>Ported "AutoStretch" contrast-enhancement mode - see this file's own header comment for
/// what it actually does and what's deliberately simplified.</summary>
public static class AutoStretchStrategy
{
    public const double DefaultGamma = 1.5;
    public const double DefaultBackgroundThreshold = 0.5;
    public const double DefaultProtusStretch = 0;

    private const double MaxPixelValue = 65535;
    private const float DiskMidtoneTarget = 0.45f * 65535f;
    private const float MinPedestal = 0.005f * 65535f;
    private const float MidtoneContrastSlope = 1.25f;

    // The contrast S-curve fades from full strength to identity between these normalized-radius
    // depths (0 at the ellipse center, 1 at its limb) so it doesn't affect prominences/the limb fade.
    private const double ContrastFadeInner = 0.85;
    private const double ContrastFadeOuter = 1.05;

    private static readonly ClaheStrategy DiskClahe = new(16, 64, 1.1);
    private static readonly ClaheStrategy ProminenceClahe = new(8, 64, 0.8);

    /// <summary>Stretches <paramref name="image"/> in place. <paramref name="ellipse"/> should be the
    /// disk's ellipse in <paramref name="image"/>'s own coordinate system (e.g.
    /// <see cref="DiskGeometryCorrector.Result.CorrectedEllipse"/>) - without one, only the
    /// brightness-matching/cutoff steps run (matching astro4j's own behaviour when no ellipse metadata
    /// is attached to the image).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gamma"/> isn't greater than 1,
    /// <paramref name="backgroundThreshold"/> isn't in (0, 1], or <paramref name="protusStretch"/> is
    /// negative - the same constructor guards astro4j's own <c>AutohistogramStrategy</c> has, skipped
    /// when this was first ported since these values weren't yet user-editable. Now that
    /// <c>SolScan.Core.Processing.AutoStretchParams</c> exposes them as free-text input on the Process
    /// view, worth failing clearly rather than producing silently wrong output.</exception>
    public static void Stretch(
        float[,] image,
        Ellipse? ellipse,
        double gamma = DefaultGamma,
        double backgroundThreshold = DefaultBackgroundThreshold,
        double protusStretch = DefaultProtusStretch)
    {
        if (gamma <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(gamma), gamma, "Gamma must be greater than 1.");
        }

        if (backgroundThreshold <= 0 || backgroundThreshold > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(backgroundThreshold), backgroundThreshold, "Background threshold must be in the range (0, 1].");
        }

        if (protusStretch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(protusStretch), protusStretch, "Prominence stretch must be non-negative.");
        }

        var height = image.GetLength(0);
        var width = image.GetLength(1);

        if (ellipse is { } e)
        {
            var bgNeutralized = IterativeBgNeutralize(image, e, backgroundThreshold, width, height);
            var clahe = (float[,])bgNeutralized.Clone();
            var eclipse = CreateEclipse(bgNeutralized, e.Rescale(1.005, 1.005), width, height);

            DiskClahe.Stretch(clahe);
            ProminenceClahe.Stretch(eclipse);
            ApplyProminenceExpand(eclipse, protusStretch, width, height);

            GammaStrategy.Stretch(bgNeutralized, gamma, MaxPixelValue);

            var claheBlend = new float[height, width];
            BlendInto(clahe, eclipse, 0.6, claheBlend, width, height);
            var blended = new float[height, width];
            BlendInto(bgNeutralized, claheBlend, 0.8, blended, width, height);

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    image[y, x] = blended[y, x];
                }
            }
        }

        MaybeAdjustBrightness(image, ellipse, height, width);
        ClampToRange(image);
    }

    /// <summary>Repeatedly fits and subtracts a low-order background model that excludes the disk
    /// (rescaled 5% larger, as a margin) from its own sample selection, stopping once the fitted
    /// background level stops shrinking or 25 iterations are used up - each pass removes only a
    /// <c>1 - backgroundThreshold</c> fraction of what it finds, not all of it, so the loop converges
    /// gradually rather than in one (potentially over-aggressive) step. A separate, disk-zeroed "eclipse"
    /// buffer is neutralized alongside <paramref name="image"/>'s own working copy purely to produce the
    /// convergence/pedestal signal - its own pixel data is otherwise unused (see
    /// <see cref="CreateEclipse"/>'s call site here vs. the top-level <see cref="Stretch"/>'s own,
    /// differently-scaled one).</summary>
    private static float[,] IterativeBgNeutralize(float[,] image, Ellipse ellipse, double backgroundThreshold, int width, int height)
    {
        if (backgroundThreshold >= 1)
        {
            return image;
        }

        var rescaledEllipse = ellipse.Rescale(1.05, 1.05);
        var current = (float[,])image.Clone();
        var scratch = new float[height, width];
        var eclipseWorking = CreateEclipse(current, rescaledEllipse, width, height);
        var backgroundBuffer = new float[height, width];
        var smoothing = (float)(1 - backgroundThreshold);
        var prevBg = double.MaxValue;
        var pedestal = -1.0;
        var foundPedestal = false;
        var maxIterations = 25;

        while (--maxIterations >= 0)
        {
            Array.Copy(current, scratch, current.Length);
            BackgroundNeutralizer.NeutralizeMasked(scratch, rescaledEllipse, sigma: 1.5, smoothing, MaxPixelValue, backgroundBuffer);
            var bg = BackgroundNeutralizer.NeutralizeMasked(eclipseWorking, rescaledEllipse, sigma: 1.5, smoothing, MaxPixelValue, backgroundBuffer);
            if (bg == 0 || bg > prevBg)
            {
                break;
            }

            prevBg = bg;
            (current, scratch) = (scratch, current);
            if (pedestal < 0 || (!foundPedestal && bg < 0.5 * pedestal))
            {
                pedestal = bg;
            }
            else
            {
                foundPedestal = true;
            }
        }

        if (pedestal > 0)
        {
            var addend = (float)(pedestal / 2);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    current[y, x] += addend;
                }
            }
        }

        return current;
    }

    /// <summary>A copy of <paramref name="source"/> with everything inside <paramref name="ellipse"/>
    /// zeroed out, so a subsequent CLAHE/background-fit pass only "sees" what's outside the disk.</summary>
    private static float[,] CreateEclipse(float[,] source, Ellipse ellipse, int width, int height)
    {
        var eclipse = (float[,])source.Clone();
        var (minX, maxX, minY, maxY) = ellipse.BoundingBox();
        var x0 = System.Math.Max(0, (int)minX);
        var x1 = System.Math.Min(width - 1, (int)maxX);
        var y0 = System.Math.Max(0, (int)minY);
        var y1 = System.Math.Min(height - 1, (int)maxY);
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                if (ellipse.IsWithin(x, y))
                {
                    eclipse[y, x] = 0;
                }
            }
        }

        return eclipse;
    }

    /// <summary>Expands the prominence pass's output range via a curve through <c>(0,0)</c>,
    /// <c>(16, 16*(1+protusStretch) clamped to [16,255])</c> and <c>(65535, 65535)</c> - astro4j's own
    /// <c>ColorCurve.cachedPolynomial</c>, fitted here by exact 3-point Lagrange interpolation rather
    /// than porting that type's whole regression/caching machinery: with exactly 3 points and 3
    /// coefficients, a "regression" fit has no residual to minimize, so the two are equivalent.</summary>
    private static void ApplyProminenceExpand(float[,] data, double protusStretch, int width, int height)
    {
        var to = System.Math.Clamp(16 * (1 + protusStretch), 16, 255);
        if (to == 16)
        {
            // (0,0), (16,16) and (65535,65535) are collinear - the curve is exactly the identity, so
            // skip it. SolScan's only currently-reachable value of protusStretch (0) always lands here.
            return;
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = (float)EvaluateQuadraticThroughThreePoints(0, 0, 16, to, MaxPixelValue, MaxPixelValue, data[y, x]);
            }
        }
    }

    private static double EvaluateQuadraticThroughThreePoints(double x0, double y0, double x1, double y1, double x2, double y2, double x)
    {
        var l0 = (x - x1) * (x - x2) / ((x0 - x1) * (x0 - x2));
        var l1 = (x - x0) * (x - x2) / ((x1 - x0) * (x1 - x2));
        var l2 = (x - x0) * (x - x1) / ((x2 - x0) * (x2 - x1));
        return (y0 * l0) + (y1 * l1) + (y2 * l2);
    }

    private static void BlendInto(float[,] img1, float[,] img2, double alpha, float[,] output, int width, int height)
    {
        var beta = 1 - alpha;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                output[y, x] = (float)((alpha * img1[y, x]) + (beta * img2[y, x]));
            }
        }
    }

    /// <summary>Lifts the image so the disk's own median brightness reaches <see cref="DiskMidtoneTarget"/>
    /// via an asinh curve (gentler roll-off at the top than a sigmoid, so bright disk content keeps its
    /// dynamics instead of flattening against the maximum), then applies a cubic contrast S-curve
    /// through the pedestal/midtone/maximum that pushes darks darker and brights brighter - faded to
    /// identity outside the disk (via the ellipse's own implicit conic equation) so it doesn't crush
    /// prominences or the chromospheric limb. A no-op if the disk is already at or above the target, or
    /// if there's no usable midtone (e.g. an all-background frame).</summary>
    private static void MaybeAdjustBrightness(float[,] diskData, Ellipse? ellipseOpt, int height, int width)
    {
        var cumulative = Histogram.Of(diskData, 65536, MaxPixelValue).Cumulative();
        var p5 = cumulative.Percentile(0.05);
        var pedestal = System.Math.Max(p5, MinPedestal);
        var midtone = DiskMedianInside(diskData, ellipseOpt, width, height, pedestal);
        if (midtone <= pedestal || midtone >= DiskMidtoneTarget)
        {
            return;
        }

        var range = MaxPixelValue - pedestal;
        var tMid = (midtone - pedestal) / range;
        var tTarget = (DiskMidtoneTarget - pedestal) / range;
        var beta = SolveAsinhBeta(tMid, tTarget);
        var asinhBeta = System.Math.Log(beta + System.Math.Sqrt((beta * beta) + 1));

        var m = (double)DiskMidtoneTarget;
        var maxD = MaxPixelValue;
        var ca = (MidtoneContrastSlope - 1.0) / (m * (m - maxD));
        var cb = -ca * (maxD + m);
        var cc = 1.0 + (ca * m * maxD);

        double ellA = 0, ellB = 0, ellC = 0, ellD = 0, ellE = 0, ellF = 0, fCenter = -1;
        var hasEllipse = ellipseOpt.HasValue;
        if (hasEllipse)
        {
            var e = ellipseOpt!.Value;
            ellA = e.A;
            ellB = e.B;
            ellC = e.C;
            ellD = e.D;
            ellE = e.E;
            ellF = e.F;
            var (cx, cy) = e.Center();
            fCenter = (ellA * cx * cx) + (ellB * cx * cy) + (ellC * cy * cy) + (ellD * cx) + (ellE * cy) + ellF;
            if (fCenter >= 0)
            {
                hasEllipse = false;
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = diskData[y, x];
                if (v <= pedestal)
                {
                    continue;
                }

                var t = (v - pedestal) / range;
                var arg = beta * t;
                var asinhArg = System.Math.Log(arg + System.Math.Sqrt((arg * arg) + 1));
                var afterAsinh = pedestal + (range * asinhArg / asinhBeta);
                var contrasted = (ca * afterAsinh * afterAsinh * afterAsinh) + (cb * afterAsinh * afterAsinh) + (cc * afterAsinh);
                double result;
                if (hasEllipse)
                {
                    var fVal = (ellA * x * x) + (ellB * x * y) + (ellC * y * y) + (ellD * x) + (ellE * y) + ellF;
                    var depth = 1.0 - (fVal / fCenter);
                    var s = (depth - ContrastFadeInner) / (ContrastFadeOuter - ContrastFadeInner);
                    var fadeOut = s <= 0 ? 0.0 : s >= 1 ? 1.0 : s * s * (3 - (2 * s));
                    result = afterAsinh + ((1 - fadeOut) * (contrasted - afterAsinh));
                }
                else
                {
                    result = contrasted;
                }

                diskData[y, x] = (float)System.Math.Clamp(result, 0, MaxPixelValue);
            }
        }
    }

    private static float DiskMedianInside(float[,] data, Ellipse? ellipse, int width, int height, float pedestal)
    {
        var hist = new int[65536];
        var count = 0;
        if (ellipse is not { } e)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var v = data[y, x];
                    if (v > pedestal)
                    {
                        hist[(int)System.Math.Clamp(v, 0, 65535)]++;
                        count++;
                    }
                }
            }
        }
        else
        {
            var (minX, maxX, minY, maxY) = e.BoundingBox();
            var x0 = System.Math.Max(0, (int)minX);
            var x1 = System.Math.Min(width - 1, (int)maxX);
            var y0 = System.Math.Max(0, (int)minY);
            var y1 = System.Math.Min(height - 1, (int)maxY);
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var v = data[y, x];
                    if (e.IsWithin(x, y) && v > pedestal)
                    {
                        hist[(int)System.Math.Clamp(v, 0, 65535)]++;
                        count++;
                    }
                }
            }
        }

        if (count == 0)
        {
            return 0;
        }

        var half = count / 2;
        var cumulative = 0;
        for (var i = 0; i < hist.Length; i++)
        {
            cumulative += hist[i];
            if (cumulative >= half)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Solves <c>asinh(beta * tMid) = tTarget * asinh(beta)</c> for <c>beta</c> by bisection -
    /// the curve parameter that lifts the disk's normalized midtone <paramref name="tMid"/> exactly to
    /// the normalized target <paramref name="tTarget"/>.</summary>
    private static double SolveAsinhBeta(double tMid, double tTarget)
    {
        var lo = 0.01;
        var hi = 10000.0;
        for (var i = 0; i < 200; i++)
        {
            var beta = (lo + hi) / 2;
            var lhs = System.Math.Log((beta * tMid) + System.Math.Sqrt((beta * beta * tMid * tMid) + 1));
            var rhs = tTarget * System.Math.Log(beta + System.Math.Sqrt((beta * beta) + 1));
            if (lhs > rhs)
            {
                hi = beta;
            }
            else
            {
                lo = beta;
            }

            if (hi - lo < 1e-8)
            {
                break;
            }
        }

        return (lo + hi) / 2;
    }

    /// <summary>Safety clamp to <c>[0, MaxPixelValue]</c> - astro4j's own final step is a
    /// <c>CutoffStretchingStrategy.DEFAULT</c> with exactly these bounds; ported here as a plain clamp
    /// rather than a full type, since with those particular bounds that's all it ever does.</summary>
    private static void ClampToRange(float[,] data)
    {
        var height = data.GetLength(0);
        var width = data.GetLength(1);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = System.Math.Clamp(data[y, x], 0, (float)MaxPixelValue);
            }
        }
    }
}
