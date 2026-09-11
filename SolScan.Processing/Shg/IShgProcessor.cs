using System.Threading;
using SolScan.Core.Processing;
using SolScan.Processing.Math;

namespace SolScan.Processing.Shg;

/// <summary>
/// Processes a finished SER capture into output images, per <see cref="SolScan.Core.Processing.ProcessParams"/>.
/// This first implementation (<see cref="ShgProcessor"/>) only produces <see cref="GeneratedImageKind.Raw"/>/
/// <see cref="GeneratedImageKind.Reconstruction"/>/<see cref="GeneratedImageKind.Continuum"/> - real
/// spectral-line-curvature detection and reconstruction, but no geometry correction yet (that needs
/// ellipse fitting, a separate future piece of work) - see <see cref="ShgProcessingResult.SkippedKinds"/>.
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
/// <see cref="GeneratedImageKind.GeometryCorrected"/>/<see cref="GeneratedImageKind.GeometryCorrectedProcessed"/>
/// whenever requested) - reported explicitly rather than silently dropped.</param>
/// <param name="DetectedLinePolynomial">The fitted spectral-line-curvature polynomial, or null if
/// nothing needed reconstruction (no Raw/Reconstruction/Continuum requested).</param>
public sealed record ShgProcessingResult(
    IReadOnlyList<ProcessedImage> Images,
    IReadOnlyList<GeneratedImageKind> SkippedKinds,
    QuadraticPolynomial? DetectedLinePolynomial);
