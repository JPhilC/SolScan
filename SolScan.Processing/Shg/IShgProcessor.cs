using System.Threading;
using SolScan.Core.Processing;
using SolScan.Processing.Math;

namespace SolScan.Processing.Shg;

/// <summary>
/// Processes a finished SER capture into output images, per <see cref="SolScan.Core.Processing.ProcessParams"/>.
/// This implementation (<see cref="ShgProcessor"/>) produces real <see cref="GeneratedImageKind.Raw"/>/
/// <see cref="GeneratedImageKind.Reconstruction"/>/<see cref="GeneratedImageKind.Continuum"/>/
/// <see cref="GeneratedImageKind.GeometryCorrected"/> images - spectral-line-curvature detection,
/// reconstruction, disk-edge ellipse fitting and geometry correction are all real.
/// <see cref="GeneratedImageKind.GeometryCorrectedProcessed"/> still needs contrast enhancement (a
/// separate future piece of work) - see <see cref="ShgProcessingResult.SkippedKinds"/>.
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
/// <param name="SkippedKinds">Requested kinds this processor doesn't implement yet (currently
/// <see cref="GeneratedImageKind.GeometryCorrectedProcessed"/> whenever requested) - reported
/// explicitly rather than silently dropped.</param>
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
