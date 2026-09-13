using System.Threading;
using SolScan.Core.Processing;
using SolScan.Processing.Math;

namespace SolScan.Processing.Shg;

/// <summary>
/// Processes a finished SER capture into output images, per <see cref="SolScan.Core.Processing.ProcessParams"/>.
/// This implementation (<see cref="ShgProcessor"/>) produces real <see cref="GeneratedImageKind.Raw"/>/
/// <see cref="GeneratedImageKind.Reconstruction"/>/<see cref="GeneratedImageKind.Continuum"/>/
/// <see cref="GeneratedImageKind.GeometryCorrected"/>/<see cref="GeneratedImageKind.GeometryCorrectedProcessed"/>
/// images - spectral-line-curvature detection, reconstruction, disk-edge ellipse fitting, geometry
/// correction, and (for the last kind) every <see cref="ContrastEnhancementMode"/> - AutoStretch, CLAHE,
/// and CLAHE2 (multi-scale CLAHE, layering several tile sizes of the same CLAHE and averaging them) -
/// are all real.
/// </summary>
public interface IShgProcessor
{
    Task<ShgProcessingResult> ProcessAsync(
        string serFilePath,
        ProcessParams processParams,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <param name="Images">The images actually produced.</param>
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
public sealed record ShgProcessingResult(
    IReadOnlyList<ProcessedImage> Images,
    IReadOnlyList<GeneratedImageKind> SkippedKinds,
    QuadraticPolynomial? DetectedLinePolynomial,
    double? DetectedTiltDegrees = null,
    double? DetectedXyRatio = null);
