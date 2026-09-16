using System.Threading;
using SolScan.Core.Capture;
using SolScan.Core.Processing;
using SolScan.Processing.Color;
using SolScan.Processing.Math;
using SolScan.Processing.Spectrum;
using SolScan.Processing.Stretching;

namespace SolScan.Processing.Shg;

/// <summary>
/// <see cref="IShgProcessor"/> orchestrating <see cref="FrameAverager"/> →
/// <see cref="SpectralLineCurvatureDetector"/> → (when <see cref="SpectrumParams.DetectionMode"/> isn't
/// <see cref="LineDetectionMode.Manual"/> and a <see cref="LineIdentificationEquipment"/> was supplied)
/// <see cref="SpectralProfileExtractor"/> → <see cref="SpectralLineIdentifier"/>, reusing the same
/// average/curvature-fit this step already computed rather than re-averaging the file a second time -
/// a confident identification overrides <see cref="SpectrumParams.Ray"/> for the rest of this run
/// (the calcium-routing check below and colorization's tint/curve choice), an unconfident one falls
/// back to whatever Ray was configured, and neither ever affects reconstruction itself (which line the
/// curvature fit locks onto is independent of which named line it turns out to be) - then
/// <see cref="DiskReconstructor"/> → (when
/// <see cref="GeneratedImageKind.GeometryCorrected"/>, <see cref="GeneratedImageKind.GeometryCorrectedProcessed"/>,
/// <see cref="GeneratedImageKind.Colorized"/>, or <see cref="GeneratedImageKind.VirtualEclipse"/> is
/// requested) <see cref="DiskGeometryCorrector"/> → (for VirtualEclipse only) <see cref="Coronagraph"/>,
/// or (for the middle two) a <see cref="ContrastEnhancementMode"/>-selected stretch
/// (<see cref="AutoStretchStrategy"/>, <see cref="ClaheStrategy"/>, or <see cref="MultiScaleClaheStrategy"/> -
/// see <see cref="ApplyContrastEnhancement"/>) → (for Colorized only) <see cref="ProduceColorizedImage"/>.
/// Deliberately returns pure in-memory <see cref="ProcessedImage"/>/<see cref="ProcessedColorImage"/>
/// data (no file IO at all) rather than writing PNGs itself: SolScan has no existing image-file-writing
/// anywhere, and WPF's own <c>PngBitmapEncoder</c> (already a hard dependency of SolScan.App,
/// `PixelFormats.Gray16`/`Rgb48` well-supported) is more reliable for 16-bit imagery than
/// `System.Drawing.Common`'s well-known GDI+ save-path issues - so the actual encode/save step lives
/// in SolScan.App instead, keeping this project genuinely IO-free per its own "pure algorithms"
/// description.
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
        LineIdentificationEquipment? identificationEquipment = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Process(serFilePath, processParams, identificationEquipment, progress, cancellationToken), cancellationToken);

    private ShgProcessingResult Process(string serFilePath, ProcessParams processParams, LineIdentificationEquipment? identificationEquipment, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var requested = processParams.RequestedImages;
        var skipped = new List<GeneratedImageKind>();

        // The effective SpectrumParams for this run - starts as whatever was configured, and is
        // overridden below (Ray only) if automatic identification confidently disagrees. See this
        // class's own doc comment for why identification is skipped entirely when either condition
        // fails, and ShgProcessingResult's own doc comment for how the outcome is reported back.
        var spectrumParams = processParams.SpectrumParams;
        SpectralLineIdentificationResult? lineIdentification = null;

        var wantsRaw = requested.IsEnabled(GeneratedImageKind.Raw);
        var wantsReconstruction = requested.IsEnabled(GeneratedImageKind.Reconstruction);
        var wantsContinuum = requested.IsEnabled(GeneratedImageKind.Continuum);
        var wantsGeometryCorrected = requested.IsEnabled(GeneratedImageKind.GeometryCorrected);
        var wantsGeometryCorrectedProcessed = requested.IsEnabled(GeneratedImageKind.GeometryCorrectedProcessed);
        var wantsColorized = requested.IsEnabled(GeneratedImageKind.Colorized);
        var wantsVirtualEclipse = requested.IsEnabled(GeneratedImageKind.VirtualEclipse);
        // GeometryCorrectedProcessed's input is the geometry-corrected image itself, so it needs the
        // same correction step run even when GeometryCorrected wasn't separately requested. Colorized's
        // own input is that same contrast-enhanced buffer (astro4j's ProcessingWorkflow computes it
        // whenever either GEOMETRY_CORRECTED_PROCESSED or COLORIZED is requested, for the same reason).
        // VirtualEclipse's own input is the plain (un-enhanced) geometry-corrected image - it only
        // needs the ellipse fit itself, not contrast enhancement - matching astro4j's own
        // ProcessingWorkflow.produceCoronagraph, which runs off WorkflowResults.GEOMETRY_CORRECTION
        // independent of whether GEOMETRY_CORRECTED_PROCESSED/COLORIZED were requested.
        var needsGeometryCorrection = wantsGeometryCorrected || wantsGeometryCorrectedProcessed || wantsColorized || wantsVirtualEclipse;
        var needsContrastEnhancement = wantsGeometryCorrectedProcessed || wantsColorized;

        var images = new List<ProcessedImage>();
        var colorImages = new List<ProcessedColorImage>();
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

                if (spectrumParams.DetectionMode != LineDetectionMode.Manual && identificationEquipment is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report("Identifying spectral line...");

                    // Same "half the frame height, capped" convention as SolScan.Tools' own annotate
                    // command and the live overlay's SpectralOverlayAnalyzer - as far as there's any
                    // real data to sample regardless, bounded so an unusually tall full-sensor capture
                    // doesn't make this scan an unbounded amount of the frame.
                    var maxShiftPixels = System.Math.Clamp((average.GetLength(0) / 2) - 1, 1, 2000);
                    var lineProfile = SpectralProfileExtractor.Extract(average, polynomial.Value, maxShiftPixels);
                    var identifier = new SpectralLineIdentifier(identificationEquipment.Instrument, identificationEquipment.PixelSizeMicrons, identificationEquipment.Binning);
                    lineIdentification = identifier.Identify(lineProfile);
                    if (lineIdentification.IdentifiedRay is { } identifiedRay)
                    {
                        spectrumParams = spectrumParams with { Ray = identifiedRay };
                    }
                }
            }

            if (wantsRaw || wantsReconstruction || needsGeometryCorrection)
            {
                using var reader = _serReaderFactory();
                reader.Open(serFilePath);
                progress?.Report("Reconstructing Raw...");
                var raw = new DiskReconstructor().Reconstruct(reader, polynomial.Value, spectrumParams.PixelShift, progress, cancellationToken);
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
                    // From here on, use the effective SpectrumParams (spectrumParams), not the
                    // originally-configured processParams.SpectrumParams - see this class's own doc
                    // comment for why the two can differ.
                    var effectiveProcessParams = processParams with { SpectrumParams = spectrumParams };
                    if (wantsGeometryCorrected)
                    {
                        images.Add(BuildProcessedImage(GeneratedImageKind.GeometryCorrected, geometryResult.Value.Pixels, nativeBitDepth));
                    }

                    if (wantsVirtualEclipse)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report("Generating virtual eclipse...");
                        var scaled = ScaleToContainerRange(geometryResult.Value.Pixels, maxPixelValue);
                        var eclipse = Coronagraph.Produce(scaled, geometryResult.Value.CorrectedEllipse);
                        images.Add(BuildProcessedImageFromFullRange(GeneratedImageKind.VirtualEclipse, eclipse));
                    }

                    if (needsContrastEnhancement)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report($"Applying {processParams.ContrastEnhancement} contrast enhancement...");
                        var processed = ApplyContrastEnhancement(geometryResult.Value.Pixels, geometryResult.Value.CorrectedEllipse, effectiveProcessParams, maxPixelValue);
                        if (wantsGeometryCorrectedProcessed)
                        {
                            images.Add(BuildProcessedImageFromFullRange(GeneratedImageKind.GeometryCorrectedProcessed, processed));
                        }

                        if (wantsColorized)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            progress?.Report("Colorizing...");
                            var blackPointOnContainerScale = (float)(geometryResult.Value.BlackPoint * 65535.0 / maxPixelValue);
                            var colorized = ProduceColorizedImage(processed, blackPointOnContainerScale, spectrumParams.Ray);
                            if (colorized is not null)
                            {
                                colorImages.Add(colorized);
                            }
                        }
                    }
                }
            }

            if (wantsContinuum)
            {
                using var reader = _serReaderFactory();
                reader.Open(serFilePath);
                progress?.Report("Reconstructing Continuum...");
                var continuum = new DiskReconstructor().Reconstruct(reader, polynomial.Value, spectrumParams.ContinuumShift, progress, cancellationToken);
                images.Add(BuildProcessedImage(GeneratedImageKind.Continuum, continuum, reader.Header.PixelDepth));
            }
        }

        progress?.Report("Done.");
        var identificationBestGuess = lineIdentification?.AllCandidates.Count > 0 ? lineIdentification.AllCandidates[0].Ray : null;
        return new ShgProcessingResult(
            images,
            skipped,
            polynomial,
            geometryResult?.TiltDegrees,
            geometryResult?.XyRatio,
            colorImages,
            identificationBestGuess,
            lineIdentification?.BestScore,
            lineIdentification?.IdentifiedRay is not null);
    }

    /// <summary>Produces <see cref="GeneratedImageKind.Colorized"/> from <paramref name="processed"/> -
    /// the same contrast-enhanced buffer <see cref="GeneratedImageKind.GeometryCorrectedProcessed"/>
    /// is built from - direct port of astro4j's <c>ProcessingWorkflow.produceColorizedImage</c>: an
    /// arcsinh stretch anchored to the disk's own background level, a percentile clip, then either
    /// <paramref name="ray"/>'s fixed <see cref="SpectralRay.ColorCurve"/> or (for every other named
    /// ray) <see cref="SpectralRay.ToRgb"/>'s wavelength-approximated tint. Returns null - no image
    /// produced at all - for <see cref="SpectralRay.Other"/>, which has no colour to derive either
    /// way, matching astro4j's own silent no-op for that case.</summary>
    private static ProcessedColorImage? ProduceColorizedImage(float[,] processed, float blackPoint, SpectralRay ray)
    {
        var working = (float[,])processed.Clone();
        new ArcsinhStretchingStrategy(blackPoint, stretch: 2f).Stretch(working);
        new PercentileStretchStrategy(0, 99.9).Stretch(working);

        (float[,] R, float[,] G, float[,] B) rgb;
        if (ray.ColorCurve is { } curve)
        {
            rgb = Colorize.WithCurve(working, curve);
        }
        else if (ray.WavelengthAngstroms > 0)
        {
            rgb = Colorize.WithWavelengthRgb(working, ray.ToRgb());
        }
        else
        {
            return null;
        }

        return BuildProcessedColorImage(rgb);
    }

    /// <summary>Like <see cref="BuildProcessedImageFromFullRange"/>, but for the three channels
    /// <see cref="ProduceColorizedImage"/> produces - all already in the 16-bit container range.</summary>
    private static ProcessedColorImage BuildProcessedColorImage((float[,] R, float[,] G, float[,] B) rgb)
    {
        var height = rgb.R.GetLength(0);
        var width = rgb.R.GetLength(1);
        var r = new ushort[height, width];
        var g = new ushort[height, width];
        var b = new ushort[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                r[y, x] = (ushort)System.Math.Clamp(rgb.R[y, x], 0, 65535);
                g[y, x] = (ushort)System.Math.Clamp(rgb.G[y, x], 0, 65535);
                b[y, x] = (ushort)System.Math.Clamp(rgb.B[y, x], 0, 65535);
            }
        }

        return new ProcessedColorImage(GeneratedImageKind.Colorized, width, height, r, g, b);
    }

    /// <summary>Rescales the (native-ADC-range) geometry-corrected pixels up to the 16-bit "container"
    /// range every ported stretching algorithm here assumes (matching astro4j's own <c>ImageWrapper32</c>
    /// convention, and this class's own <see cref="BuildProcessedImage"/> scaling) - shared by
    /// <see cref="ApplyContrastEnhancement"/> and <see cref="Coronagraph.Produce"/>'s own call site,
    /// which needs the same rescale before running on the plain (un-enhanced) geometry-corrected
    /// image.</summary>
    private static float[,] ScaleToContainerRange(float[,] pixels, int nativeMaxPixelValue)
    {
        var height = pixels.GetLength(0);
        var width = pixels.GetLength(1);
        var scale = 65535.0 / nativeMaxPixelValue;
        var scaled = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                scaled[y, x] = (float)(pixels[y, x] * scale);
            }
        }

        return scaled;
    }

    /// <summary>Rescales via <see cref="ScaleToContainerRange"/> before running the requested
    /// <see cref="ContrastEnhancementMode"/>. Rescaling pixel <em>values</em> doesn't affect
    /// <paramref name="ellipse"/>'s spatial coordinates, so it's passed through unchanged.</summary>
    private static float[,] ApplyContrastEnhancement(float[,] pixels, Ellipse ellipse, ProcessParams processParams, int nativeMaxPixelValue)
    {
        var working = ScaleToContainerRange(pixels, nativeMaxPixelValue);

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
