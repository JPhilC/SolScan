using System.Threading;
using SolScan.Core.Equipment;
using SolScan.Core.Processing;
using SolScan.Processing.Math;

namespace SolScan.Processing.Shg;

/// <summary>
/// Processes a finished SER capture into output images, per <see cref="SolScan.Core.Processing.ProcessParams"/>.
/// This implementation (<see cref="ShgProcessor"/>) produces every <see cref="GeneratedImageKind"/>
/// value SolScan declares - spectral-line-curvature detection, reconstruction, disk-edge ellipse
/// fitting, geometry correction, contrast enhancement (every <see cref="ContrastEnhancementMode"/> -
/// AutoStretch, CLAHE, and CLAHE2), colorization (a fixed colour curve for H-alpha, a
/// wavelength-approximated tint for every other named line), and the virtual-eclipse/coronagraph view
/// (<see cref="GeneratedImageKind.VirtualEclipse"/>) are all real. Also real: automatic spectral-line
/// identification (<see cref="SolScan.Processing.Spectrum.SpectralLineIdentifier"/>, the same
/// already-validated-against-real-captures core the live Capture-view overlay and <c>SolScan.Tools</c>'
/// <c>annotate</c> command use) when <see cref="SpectrumParams.DetectionMode"/> isn't
/// <see cref="LineDetectionMode.Manual"/> and <paramref name="identificationEquipment"/> is supplied -
/// see <see cref="ShgProcessor"/>'s own doc comment for exactly where this slots into the pipeline.
/// </summary>
public interface IShgProcessor
{
    /// <param name="identificationEquipment">The optics needed to auto-identify which named
    /// <see cref="SpectralRay"/> the capture studies - null skips identification entirely regardless of
    /// <see cref="SpectrumParams.DetectionMode"/> (the pre-existing, always-manual behaviour), since
    /// there's nothing to compute dispersion against without it. A caller with no real equipment
    /// metadata for a given recording (e.g. no <c>.equipment.json</c> sidecar) can still supply a
    /// reasonable default here rather than leaving this null - see <c>SolScan.Tools</c>' own
    /// <c>annotate</c> command for the established fallback (a Sol'Ex-standard SHG, 2µm pixel size,
    /// no binning).</param>
    Task<ShgProcessingResult> ProcessAsync(
        string serFilePath,
        ProcessParams processParams,
        LineIdentificationEquipment? identificationEquipment = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>The optics <see cref="SolScan.Processing.Spectrum.SpectralLineIdentifier"/> needs to compute
/// dispersion - a recording's own real equipment where known, or a documented fallback otherwise (see
/// <see cref="IShgProcessor.ProcessAsync"/>'s own doc comment).</summary>
public sealed record LineIdentificationEquipment(SpectrographProfile Instrument, double PixelSizeMicrons, int Binning = 1);

/// <param name="Images">The mono images actually produced.</param>
/// <param name="SkippedKinds">Requested kinds this processor doesn't implement yet - currently always
/// empty, since every <see cref="GeneratedImageKind"/> (and every <see cref="ContrastEnhancementMode"/>
/// of <see cref="GeneratedImageKind.GeometryCorrectedProcessed"/>) is now implemented. Kept for whenever
/// a future kind needs "reported explicitly rather than silently dropped" treatment.</param>
/// <param name="DetectedLinePolynomial">The fitted spectral-line-curvature polynomial, or null if
/// nothing needed reconstruction (no Raw/Reconstruction/Continuum/GeometryCorrected requested).</param>
/// <param name="DetectedTiltDegrees">The disk tilt geometry correction removed, in degrees, or null
/// if <see cref="GeneratedImageKind.GeometryCorrected"/> wasn't requested - astro4j's own "detected
/// geometry" info-panel figure; SolScan has no such panel yet, but the value costs nothing extra to
/// carry for when it does.</param>
/// <param name="DetectedXyRatio">The disk's detected X/Y aspect ratio, or null under the same
/// condition as <paramref name="DetectedTiltDegrees"/> - the other half of that same info panel.</param>
/// <param name="ColorImages">The colour images actually produced - currently just
/// <see cref="GeneratedImageKind.Colorized"/>, and only when the studied ray has a usable colour (see
/// <see cref="SolScan.Core.Processing.SpectralRay.Other"/>'s own doc comment for the one case where a
/// requested Colorized image is silently not produced, matching astro4j's own behaviour). Null rather
/// than defaulting to an empty list purely because a record's default value for a reference-typed
/// parameter must be a compile-time constant - treat it the same as an empty list.</param>
/// <param name="IdentifiedRay">The winning candidate from automatic spectral-line identification - the
/// best guess even when not confident (see <see cref="SolScan.Processing.Spectrum.SpectralLineIdentificationResult"/>'s
/// own doc comment) - or null if identification wasn't attempted at all (Manual detection mode, or no
/// <see cref="LineIdentificationEquipment"/> supplied).</param>
/// <param name="IdentificationScore">The winning candidate's raw correlation score, or null under the
/// same condition as <paramref name="IdentifiedRay"/>.</param>
/// <param name="IdentificationConfident">True only when identification actually cleared the confidence
/// gate and was used in place of <see cref="SpectrumParams.Ray"/> for this run's contrast-enhancement
/// calcium check and colorization tint - false both when identification wasn't attempted and when it
/// ran but wasn't confident (in which case <paramref name="IdentifiedRay"/> is still the best guess,
/// just not the one actually used).</param>
public sealed record ShgProcessingResult(
    IReadOnlyList<ProcessedImage> Images,
    IReadOnlyList<GeneratedImageKind> SkippedKinds,
    QuadraticPolynomial? DetectedLinePolynomial,
    double? DetectedTiltDegrees = null,
    double? DetectedXyRatio = null,
    IReadOnlyList<ProcessedColorImage>? ColorImages = null,
    SpectralRay? IdentifiedRay = null,
    double? IdentificationScore = null,
    bool IdentificationConfident = false);
