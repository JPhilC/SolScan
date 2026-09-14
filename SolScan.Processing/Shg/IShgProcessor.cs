using System.Threading;
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
/// (<see cref="GeneratedImageKind.VirtualEclipse"/>) are all real.
/// </summary>
public interface IShgProcessor
{
    Task<ShgProcessingResult> ProcessAsync(
        string serFilePath,
        ProcessParams processParams,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

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
public sealed record ShgProcessingResult(
    IReadOnlyList<ProcessedImage> Images,
    IReadOnlyList<GeneratedImageKind> SkippedKinds,
    QuadraticPolynomial? DetectedLinePolynomial,
    double? DetectedTiltDegrees = null,
    double? DetectedXyRatio = null,
    IReadOnlyList<ProcessedColorImage>? ColorImages = null);
