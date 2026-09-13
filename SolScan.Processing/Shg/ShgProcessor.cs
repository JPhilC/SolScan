using System.Threading;
using SolScan.Core.Capture;
using SolScan.Core.Processing;
using SolScan.Processing.Math;
using SolScan.Processing.Stretching;

namespace SolScan.Processing.Shg;

/// <summary>
/// <see cref="IShgProcessor"/> orchestrating <see cref="FrameAverager"/> →
/// <see cref="SpectralLineCurvatureDetector"/> → <see cref="DiskReconstructor"/> → (when
/// <see cref="GeneratedImageKind.GeometryCorrected"/> or
/// <see cref="GeneratedImageKind.GeometryCorrectedProcessed"/> is requested)
/// <see cref="DiskGeometryCorrector"/> → (for the latter only) a
/// <see cref="ContrastEnhancementMode"/>-selected stretch (<see cref="AutoStretchStrategy"/>,
/// <see cref="ClaheStrategy"/>, or <see cref="MultiScaleClaheStrategy"/> - see
/// <see cref="ApplyContrastEnhancement"/>).
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

        var wantsRaw = requested.IsEnabled(GeneratedImageKind.Raw);
        var wantsReconstruction = requested.IsEnabled(GeneratedImageKind.Reconstruction);
        var wantsContinuum = requested.IsEnabled(GeneratedImageKind.Continuum);
        var wantsGeometryCorrected = requested.IsEnabled(GeneratedImageKind.GeometryCorrected);
        var wantsGeometryCorrectedProcessed = requested.IsEnabled(GeneratedImageKind.GeometryCorrectedProcessed);
        // GeometryCorrectedProcessed's input is the geometry-corrected image itself, so it needs the
        // same correction step run even when GeometryCorrected wasn't separately requested.
        var needsGeometryCorrection = wantsGeometryCorrected || wantsGeometryCorrectedProcessed;

        var images = new List<ProcessedImage>();
        QuadraticPolynomial? polynomial = null;
        DiskGeometryCorrector.Result? geometryResult = null;

        if (wantsRaw || wantsReconstruction || wantsContinuum || needsGeometryCorrection)
        {
            using (var averagingReader = _serReaderFactory())
            {
                averagingReader.Open(serFilePath);
                var average = new FrameAverager().ComputeAverage(averagingReader, progress, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("Detecting spectral line...");
                polynomial = new SpectralLineCurvatureDetector().Detect(average);
            }

            if (wantsRaw || wantsReconstruction || needsGeometryCorrection)
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

                if (needsGeometryCorrection)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report("Fitting disk ellipse and correcting geometry...");
                    var maxPixelValue = (1 << nativeBitDepth) - 1;
                    geometryResult = DiskGeometryCorrector.Correct(raw, processParams.GeometryParams, maxPixelValue);
                    if (wantsGeometryCorrected)
                    {
                        images.Add(BuildProcessedImage(GeneratedImageKind.GeometryCorrected, geometryResult.Value.Pixels, nativeBitDepth));
                    }

                    if (wantsGeometryCorrectedProcessed)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report($"Applying {processParams.ContrastEnhancement} contrast enhancement...");
                        var processed = ApplyContrastEnhancement(geometryResult.Value.Pixels, geometryResult.Value.CorrectedEllipse, processParams, maxPixelValue);
                        images.Add(BuildProcessedImageFromFullRange(GeneratedImageKind.GeometryCorrectedProcessed, processed));
                    }
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

    /// <summary>Rescales the (native-ADC-range) geometry-corrected pixels up to the 16-bit "container"
    /// range every ported stretching algorithm here assumes (matching astro4j's own <c>ImageWrapper32</c>
    /// convention, and this class's own <see cref="BuildProcessedImage"/> scaling) before running the
    /// requested <see cref="ContrastEnhancementMode"/>. Rescaling pixel <em>values</em> doesn't affect
    /// <paramref name="ellipse"/>'s spatial coordinates, so it's passed through unchanged.</summary>
    private static float[,] ApplyContrastEnhancement(float[,] pixels, Ellipse ellipse, ProcessParams processParams, int nativeMaxPixelValue)
    {
        var height = pixels.GetLength(0);
        var width = pixels.GetLength(1);
        var scale = 65535.0 / nativeMaxPixelValue;
        var working = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                working[y, x] = (float)(pixels[y, x] * scale);
            }
        }

        // astro4j's own AUTO picks CLAHE for a calcium-line capture (K or H), AutoStretch otherwise -
        // not an image-analysis "detection" at all, just a check against whichever line SpectrumParams.Ray
        // is already set to (currently always picked by hand - see that field's own doc comment).
        var studiedRay = processParams.SpectrumParams.Ray;
        var isCalcium = studiedRay == SpectralRay.CalciumK || studiedRay == SpectralRay.CalciumH;
        var mode = processParams.ContrastEnhancement;
        var effectiveMode = mode == ContrastEnhancementMode.Auto
            ? (isCalcium ? ContrastEnhancementMode.Clahe : ContrastEnhancementMode.AutoStretch)
            : mode;
        switch (effectiveMode)
        {
            case ContrastEnhancementMode.AutoStretch:
                var autoStretch = processParams.AutoStretchParams;
                AutoStretchStrategy.Stretch(working, ellipse, autoStretch.Gamma, autoStretch.BackgroundThreshold, autoStretch.ProtusStretch);
                break;
            case ContrastEnhancementMode.Clahe:
                var clahe = processParams.ClaheParams;
                new ClaheStrategy(clahe.TileSize, clahe.Bins, clahe.Clipping).Stretch(working);
                break;
            case ContrastEnhancementMode.Clahe2:
                MultiScaleClaheStrategy.Stretch(working, ellipse, processParams.Clahe2Params.Clipping);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(processParams), mode, "Unhandled ContrastEnhancementMode.");
        }

        return working;
    }

    /// <summary>Like <see cref="BuildProcessedImage"/>, but for pixel data that's already in the 16-bit
    /// container range (0-65535) - <see cref="ApplyContrastEnhancement"/>'s output - rather than needing
    /// the native-sensor-range scale-up.</summary>
    private static ProcessedImage BuildProcessedImageFromFullRange(GeneratedImageKind kind, float[,] pixels)
    {
        var height = pixels.GetLength(0);
        var width = pixels.GetLength(1);
        var scaled = new ushort[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                scaled[y, x] = (ushort)System.Math.Clamp(pixels[y, x], 0, 65535);
            }
        }

        return new ProcessedImage(kind, width, height, scaled);
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
