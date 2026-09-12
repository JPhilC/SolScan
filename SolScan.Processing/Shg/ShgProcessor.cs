using System.Threading;
using SolScan.Core.Capture;
using SolScan.Core.Processing;
using SolScan.Processing.Math;

namespace SolScan.Processing.Shg;

/// <summary>
/// <see cref="IShgProcessor"/> orchestrating <see cref="FrameAverager"/> →
/// <see cref="SpectralLineCurvatureDetector"/> → <see cref="DiskReconstructor"/> → (when
/// <see cref="GeneratedImageKind.GeometryCorrected"/> is requested) <see cref="DiskGeometryCorrector"/>.
/// Deliberately returns pure in-memory <see cref="ProcessedImage"/> data (no file IO at all) rather
/// than writing PNGs itself: SolScan has no existing image-file-writing anywhere, and WPF's own
/// <c>PngBitmapEncoder</c> (already a hard dependency of SolScan.App, `PixelFormats.Gray16`
/// well-supported) is more reliable for 16-bit grayscale than `System.Drawing.Common`'s well-known
/// GDI+ save-path issues for that format - so the actual encode/save step lives in SolScan.App
/// instead, keeping this project genuinely IO-free per its own "pure algorithms" description.
/// </summary>
public sealed class ShgProcessor : IShgProcessor
{
    private readonly Func<ISerReader> _serReaderFactory;

    public ShgProcessor(Func<ISerReader> serReaderFactory)
    {
        _serReaderFactory = serReaderFactory;
    }

    public Task<ShgProcessingResult> ProcessAsync(
        string serFilePath,
        ProcessParams processParams,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Process(serFilePath, processParams, progress, cancellationToken), cancellationToken);

    private ShgProcessingResult Process(string serFilePath, ProcessParams processParams, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var requested = processParams.RequestedImages;
        var skipped = new List<GeneratedImageKind>();
        if (requested.IsEnabled(GeneratedImageKind.GeometryCorrectedProcessed))
        {
            // Still needs contrast enhancement (CLAHE/AutoStretch) - a genuinely separate piece of
            // work from geometry correction itself, same "one algorithm per slice" reasoning that kept
            // ellipse fitting its own deferred step before this. See SolScan CLAUDE.md.
            skipped.Add(GeneratedImageKind.GeometryCorrectedProcessed);
        }

        var wantsRaw = requested.IsEnabled(GeneratedImageKind.Raw);
        var wantsReconstruction = requested.IsEnabled(GeneratedImageKind.Reconstruction);
        var wantsContinuum = requested.IsEnabled(GeneratedImageKind.Continuum);
        var wantsGeometryCorrected = requested.IsEnabled(GeneratedImageKind.GeometryCorrected);

        var images = new List<ProcessedImage>();
        QuadraticPolynomial? polynomial = null;
        DiskGeometryCorrector.Result? geometryResult = null;

        if (wantsRaw || wantsReconstruction || wantsContinuum || wantsGeometryCorrected)
        {
            using (var averagingReader = _serReaderFactory())
            {
                averagingReader.Open(serFilePath);
                var average = new FrameAverager().ComputeAverage(averagingReader, progress, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("Detecting spectral line...");
                polynomial = new SpectralLineCurvatureDetector().Detect(average);
            }

            if (wantsRaw || wantsReconstruction || wantsGeometryCorrected)
            {
                using var reader = _serReaderFactory();
                reader.Open(serFilePath);
                progress?.Report("Reconstructing Raw...");
                var raw = new DiskReconstructor().Reconstruct(reader, polynomial.Value, processParams.SpectrumParams.PixelShift, progress, cancellationToken);
                var nativeBitDepth = reader.Header.PixelDepth;

                if (wantsRaw || wantsReconstruction)
                {
                    var rawImage = BuildProcessedImage(GeneratedImageKind.Raw, raw, nativeBitDepth);
                    if (wantsRaw)
                    {
                        images.Add(rawImage);
                    }

                    if (wantsReconstruction)
                    {
                        // JSolex's "Reconstruction" is a progressive *live-display* variant of the same
                        // reconstruction, not a separately computed image - SolScan has no live progress
                        // view yet to make that distinction meaningful, so it's saved as the same data.
                        images.Add(rawImage with { Kind = GeneratedImageKind.Reconstruction });
                    }
                }

                if (wantsGeometryCorrected)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report("Fitting disk ellipse and correcting geometry...");
                    var maxPixelValue = (1 << nativeBitDepth) - 1;
                    geometryResult = DiskGeometryCorrector.Correct(raw, processParams.GeometryParams, maxPixelValue);
                    images.Add(BuildProcessedImage(GeneratedImageKind.GeometryCorrected, geometryResult.Value.Pixels, nativeBitDepth));
                }
            }

            if (wantsContinuum)
            {
                using var reader = _serReaderFactory();
                reader.Open(serFilePath);
                progress?.Report("Reconstructing Continuum...");
                var continuum = new DiskReconstructor().Reconstruct(reader, polynomial.Value, processParams.SpectrumParams.ContinuumShift, progress, cancellationToken);
                images.Add(BuildProcessedImage(GeneratedImageKind.Continuum, continuum, reader.Header.PixelDepth));
            }
        }

        progress?.Report("Done.");
        return new ShgProcessingResult(images, skipped, polynomial, geometryResult?.TiltDegrees, geometryResult?.XyRatio);
    }

    /// <summary>Scales native sensor range (<c>(1 &lt;&lt; nativeBitDepth) - 1</c>, matching
    /// <see cref="SolScan.Core.Camera.FramePreview"/>'s own convention) up to a 16-bit container - no
    /// contrast stretch/percentile clipping applied here (that's the deferred
    /// <see cref="ContrastEnhancementMode"/>-driven *Processed variant's job).</summary>
    private static ProcessedImage BuildProcessedImage(GeneratedImageKind kind, float[,] pixels, int nativeBitDepth)
    {
        var height = pixels.GetLength(0);
        var width = pixels.GetLength(1);
        var maxNative = (1 << nativeBitDepth) - 1;
        var scale = 65535.0 / maxNative;
        var scaled = new ushort[height, width];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                scaled[y, x] = (ushort)System.Math.Clamp(pixels[y, x] * scale, 0, 65535);
            }
        }

        return new ProcessedImage(kind, width, height, scaled);
    }
}
