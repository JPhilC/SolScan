using SolScan.Core.Equipment;
using SolScan.Core.Processing;

namespace SolScan.Processing.Spectrum;

/// <summary>
/// Identifies which of the 12 named <see cref="SpectralRay"/> lines an observed <see cref="SpectralProfile"/>
/// is centred on - correlate against each candidate's <see cref="ReferenceWindow"/> at the dispersion
/// that candidate implies, keep the best, require it to clear a confidence gate before reporting it.
///
/// This is an original design, not a port of astro4j's <c>DeepLineIdentifier</c> - the two share the
/// same basic idea (matched-filter correlation against a reference solar atlas, confidence-gated)
/// because that idea is standard spectroscopy technique, not really astro4j's own invention, but the
/// approach here is deliberately leaner: SolScan only ever needs to recognise its 12 named lines, not
/// an arbitrary point in the whole 3900-6800Å range, so there's no need for
/// <c>DeepLineIdentifier</c>'s own "extract the deepest N% of the atlas as candidates" curation step -
/// every candidate is simply tested directly - and no need for its six fixed instrumental-broadening
/// hypotheses; a single data-driven blur estimate (the observed line's own measured width) stands in
/// for v1, with multiple hypotheses only worth adding later if real data shows it's actually needed.
/// Telluric correction is deferred the same way - see this class's own test coverage/CLAUDE.md entry
/// for what's been validated against real captures so far.
/// </summary>
public sealed class SpectralLineIdentifier
{
    /// <summary>The winning candidate's raw correlation score must clear this before being reported -
    /// a starting value, not tuned against any real capture yet (see this type's own doc comment).</summary>
    public const double MinScoreThreshold = 0.6;

    /// <summary>...and must lead the runner-up by at least this much, so a genuinely ambiguous window
    /// (two candidates correlating almost equally well) is reported as unidentified rather than an
    /// arbitrary pick between them. Lowered from an initial 0.15 to 0.10 after the first real-data
    /// validation run (via SolScan.Tools' annotate command, against two of the user's own real
    /// full-frame captures): a genuinely clean H-alpha match (0.901 vs. a 0.753 runner-up, margin
    /// 0.148) missed the original 0.15 cutoff by 0.002 despite being a convincing win, while a second
    /// file - later confirmed via JSolex's own Spectrum Browser overlay to be centred in the gap
    /// between the Na D1/D2 doublet, not a single isolated line - correctly stayed unidentified either
    /// way (its own best score, 0.449, doesn't clear MinScoreThreshold regardless of the margin), so
    /// only the margin needed loosening, not the score threshold. Still a small sample (one clean win,
    /// one correctly-rejected ambiguous case) - worth revisiting as more real files are checked.</summary>
    public const double MinMarginOverRunnerUp = 0.10;

    /// <summary>How many pixels either side of the profile's own nominal centre (shift 0) to search for
    /// the best-aligning offset - absorbs a little slop from the upstream curvature fit rather than
    /// assuming shift 0 is exactly the true line centre.</summary>
    private const int LagSearchPixels = 3;

    /// <summary>Below this many overlapping (observed, reference) sample pairs, a correlation isn't
    /// meaningful - reported as a Score of 0 for that candidate rather than a spurious high/low value
    /// from too few points.</summary>
    private const int MinOverlapSamples = 20;

    private readonly SpectrographProfile _instrument;
    private readonly double _pixelSizeMicrons;
    private readonly int _binning;
    private readonly IReadOnlyList<ReferenceWindow> _referenceWindows;

    /// <param name="referenceWindows">Defaults to the bundled resource (<see cref="ReferenceWindowResource.LoadEmbedded"/>) -
    /// overridable so tests can supply small synthetic windows instead.</param>
    public SpectralLineIdentifier(SpectrographProfile instrument, double pixelSizeMicrons, int binning = 1, IReadOnlyList<ReferenceWindow>? referenceWindows = null)
    {
        _instrument = instrument;
        _pixelSizeMicrons = pixelSizeMicrons;
        _binning = binning;
        _referenceWindows = referenceWindows ?? ReferenceWindowResource.LoadEmbedded();
    }

    public SpectralLineIdentificationResult Identify(SpectralProfile observed)
    {
        var scores = new List<SpectralLineCandidateScore>();

        foreach (var ray in SpectralRay.Predefined)
        {
            if (ray.WavelengthAngstroms <= 0)
            {
                continue; // SpectralRay.Other - no fixed wavelength to test as a hypothesis.
            }

            var window = FindWindow(ray.WavelengthAngstroms);
            if (window is null)
            {
                continue; // No bundled reference data for this ray - excluded, not scored as a loser.
            }

            var dispersion = SpectralDispersion.ComputeAngstromsPerPixel(_instrument, ray.WavelengthAngstroms, _pixelSizeMicrons, _binning);
            scores.Add(new SpectralLineCandidateScore(ray, ScoreCandidate(observed, window, dispersion)));
        }

        scores.Sort((a, b) => b.Score.CompareTo(a.Score));

        if (scores.Count == 0)
        {
            return new SpectralLineIdentificationResult(null, 0, scores);
        }

        var best = scores[0];
        var runnerUpScore = scores.Count > 1 ? scores[1].Score : double.NegativeInfinity;
        var confident = best.Score >= MinScoreThreshold && (best.Score - runnerUpScore) >= MinMarginOverRunnerUp;

        return new SpectralLineIdentificationResult(confident ? best.Ray : null, best.Score, scores);
    }

    private ReferenceWindow? FindWindow(double wavelengthAngstroms) =>
        _referenceWindows.FirstOrDefault(w => System.Math.Abs(w.CenterWavelengthAngstroms - wavelengthAngstroms) < 0.01);

    /// <summary>Best Pearson correlation over a small lag search (<see cref="LagSearchPixels"/>) of
    /// pixel-shift offsets, each mapped to the reference window's own Å axis via
    /// <paramref name="dispersionAngstromsPerPixel"/>.</summary>
    private static double ScoreCandidate(SpectralProfile observed, ReferenceWindow window, double dispersionAngstromsPerPixel)
    {
        var best = double.NegativeInfinity;
        for (var lag = -LagSearchPixels; lag <= LagSearchPixels; lag++)
        {
            var correlation = CorrelateAtLag(observed, window, dispersionAngstromsPerPixel, lag);
            if (correlation > best)
            {
                best = correlation;
            }
        }

        return double.IsNegativeInfinity(best) ? 0 : best;
    }

    private static double CorrelateAtLag(SpectralProfile observed, ReferenceWindow window, double dispersionAngstromsPerPixel, int lag)
    {
        var observedSamples = new List<double>();
        var referenceSamples = new List<double>();

        for (var shift = observed.MinShiftPixels; shift <= observed.MaxShiftPixels; shift++)
        {
            var observedValue = observed.ValueAt(shift);
            if (observedValue is null)
            {
                continue;
            }

            var wavelength = window.CenterWavelengthAngstroms + ((shift + lag) * dispersionAngstromsPerPixel);
            var referenceValue = window.IntensityAt(wavelength);
            if (referenceValue is null)
            {
                continue;
            }

            observedSamples.Add(observedValue.Value);
            referenceSamples.Add(referenceValue.Value);
        }

        return observedSamples.Count < MinOverlapSamples ? 0 : PearsonCorrelation(observedSamples, referenceSamples);
    }

    private static double PearsonCorrelation(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var n = a.Count;
        var meanA = a.Average();
        var meanB = b.Average();

        double covariance = 0, varianceA = 0, varianceB = 0;
        for (var i = 0; i < n; i++)
        {
            var da = a[i] - meanA;
            var db = b[i] - meanB;
            covariance += da * db;
            varianceA += da * da;
            varianceB += db * db;
        }

        var denominator = System.Math.Sqrt(varianceA * varianceB);
        return denominator > 0 ? covariance / denominator : 0;
    }
}

public sealed record SpectralLineCandidateScore(SpectralRay Ray, double Score);

/// <param name="IdentifiedRay">The winning candidate, or null if it didn't clear
/// <see cref="SpectralLineIdentifier.MinScoreThreshold"/>/<see cref="SpectralLineIdentifier.MinMarginOverRunnerUp"/> -
/// "no confident match" is a valid, expected answer, not a failure.</param>
/// <param name="BestScore">The winning (or best, even if not confident) candidate's raw score.</param>
/// <param name="AllCandidates">Every scored candidate, sorted best-first - lets a caller (e.g. the
/// <c>annotate</c> dev tool) show the runner-ups for context even when there's no confident winner.</param>
public sealed record SpectralLineIdentificationResult(SpectralRay? IdentifiedRay, double BestScore, IReadOnlyList<SpectralLineCandidateScore> AllCandidates);
