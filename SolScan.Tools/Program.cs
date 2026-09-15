using SolScan.Core.Capture;
using SolScan.Core.Equipment;
using SolScan.Infrastructure.Capture;
using SolScan.Processing.Shg;
using SolScan.Processing.Spectrum;
using SolScan.Tools;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

return args[0] switch
{
    "extract-atlas" => RunExtractAtlas(args[1..]),
    "annotate" => RunAnnotate(args[1..]),
    _ => Unknown(),
};

static int Unknown()
{
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        SolScan.Tools - dev-only utilities, never shipped to end users.

        Usage:
          extract-atlas <path-to-atlasvi.dat> [output-path]
              Regenerates SolScan.Processing's bundled reference-window resource from a local
              BASS2000 atlasvi.dat file. Run once (or whenever the extraction logic changes) -
              not needed to build or run SolScan itself.

          annotate <path-to-full-frame.ser> [--expected <RayLabel>] [--pixel-size <microns>] [--binning <n>]
              Averages the file, extracts a spectral profile, and reports which named line
              SpectralLineIdentifier thinks it's centred on - for validating the identifier
              against your own real captures. Reads equipment info from the file's own
              .equipment.json sidecar if present; --pixel-size/--binning override or supply it
              when there's no sidecar.
        """);
}

static int RunExtractAtlas(string[] args)
{
    if (args.Length < 1)
    {
        Console.Error.WriteLine("Usage: extract-atlas <path-to-atlasvi.dat> [output-path]");
        return 1;
    }

    var atlasPath = args[0];
    var outputPath = args.Length > 1
        ? args[1]
        : Path.Combine("..", "SolScan.Processing", "Spectrum", "Resources", "reference-windows.bin");

    var progress = new Progress<string>(Console.WriteLine);
    var windows = AtlasExtractor.ExtractWindows(atlasPath, progress);

    var fullOutputPath = Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
    using (var stream = File.Create(fullOutputPath))
    {
        ReferenceWindowResource.Save(stream, windows);
    }

    var sizeKb = new FileInfo(fullOutputPath).Length / 1024.0;
    Console.WriteLine();
    Console.WriteLine($"Wrote {windows.Count} reference window(s) to '{fullOutputPath}' ({sizeKb:F1} KB).");
    foreach (var window in windows)
    {
        Console.WriteLine($"  {window.CenterWavelengthAngstroms,9:F2}Å  [{window.MinWavelengthAngstroms:F2}, {window.MaxWavelengthAngstroms:F2}]  {window.Intensities.Count} samples");
    }

    return 0;
}

static int RunAnnotate(string[] args)
{
    if (args.Length < 1)
    {
        Console.Error.WriteLine("Usage: annotate <path-to-full-frame.ser> [--expected <RayLabel>] [--pixel-size <microns>] [--binning <n>]");
        return 1;
    }

    var serPath = args[0];
    string? expectedLabel = null;
    double? pixelSizeOverride = null;
    int? binningOverride = null;

    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--expected" when i + 1 < args.Length:
                expectedLabel = args[++i];
                break;
            case "--pixel-size" when i + 1 < args.Length:
                pixelSizeOverride = double.Parse(args[++i]);
                break;
            case "--binning" when i + 1 < args.Length:
                binningOverride = int.Parse(args[++i]);
                break;
        }
    }

    ICaptureMetadataStore metadataStore = new JsonCaptureMetadataStore();
    var metadata = metadataStore.TryRead(serPath);

    var instrument = metadata?.Spectrograph ?? SpectrographProfile.CreateSolEx();
    if (metadata?.Spectrograph is null)
    {
        Console.WriteLine($"No equipment sidecar found (or no SHG recorded in it) - assuming {instrument.Label}.");
    }

    var pixelSizeMicrons = pixelSizeOverride ?? metadata?.Camera?.PixelSizeMicrons ?? 2.0;
    if (pixelSizeOverride is null && metadata?.Camera?.PixelSizeMicrons is null)
    {
        Console.WriteLine($"No camera pixel size known - assuming {pixelSizeMicrons}µm (ASI678MM ballpark). Pass --pixel-size to override.");
    }

    var binning = binningOverride ?? metadata?.CameraSettingsUsed?.Binning ?? 1;

    using var reader = new SerReader();
    reader.Open(serPath);
    Console.WriteLine($"'{serPath}': {reader.Header.Width}x{reader.Header.Height}, {reader.Header.PixelDepth}-bit, {reader.Header.FrameCount} frame(s).");

    var progress = new Progress<string>(Console.WriteLine);
    var average = new FrameAverager().ComputeAverage(reader, progress);
    var polynomial = new SpectralLineCurvatureDetector().Detect(average);

    var maxShiftPixels = Math.Min((reader.Header.Height / 2) - 1, 2000);
    var profile = SpectralProfileExtractor.Extract(average, polynomial, maxShiftPixels);

    var identifier = new SpectralLineIdentifier(instrument, pixelSizeMicrons, binning);
    var result = identifier.Identify(profile);

    Console.WriteLine();
    Console.WriteLine(result.IdentifiedRay is { } identified
        ? $"Identified: {identified.Label} (score {result.BestScore:F3})"
        : $"No confident match (best guess: {result.AllCandidates.FirstOrDefault()?.Ray.Label ?? "none"}, score {result.BestScore:F3})");

    Console.WriteLine("All candidates:");
    foreach (var candidate in result.AllCandidates)
    {
        Console.WriteLine($"  {candidate.Ray.Label,-22} {candidate.Score,7:F3}");
    }

    if (expectedLabel is not null)
    {
        var matched = string.Equals(result.IdentifiedRay?.Label, expectedLabel, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine();
        Console.WriteLine(matched
            ? $"PASS - matched expected '{expectedLabel}'."
            : $"FAIL - expected '{expectedLabel}', got '{result.IdentifiedRay?.Label ?? "(no confident match)"}'.");
        return matched ? 0 : 1;
    }

    return 0;
}
