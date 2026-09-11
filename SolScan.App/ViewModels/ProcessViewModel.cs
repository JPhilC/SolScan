using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SolScan.Core.Camera;
using SolScan.Core.Capture;
using SolScan.Core.Processing;
using SolScan.Processing.Shg;

namespace SolScan.App.ViewModels;

/// <summary>
/// Process stage: pick a finished SER capture, inspect its header/equipment metadata, and run it
/// through <see cref="IShgProcessor"/> - real spectral-line-curvature detection and reconstruction
/// (see <see cref="ShgProcessor"/>'s own doc comment), producing real Raw/Reconstruction/Continuum
/// output images. Geometry correction (<see cref="GeneratedImageKind.GeometryCorrected"/>/
/// <see cref="GeneratedImageKind.GeometryCorrectedProcessed"/>) needs ellipse fitting, a separate
/// piece of work not built yet - requesting either is reported back as "not yet implemented" rather
/// than silently skipped. Processing is manual (click Process) rather than automatic-on-capture-finish
/// - see CaptureViewModel for where that recording-finished moment currently has no hook to drive
/// from; that's real future work per SolScan CLAUDE.md's Phase 7.
///
/// The image dropdown/preview (<see cref="AvailableProcessedImages"/>/<see cref="SelectedProcessedImage"/>)
/// is deliberately disk-based, not tied to the most recent <see cref="ProcessAsync"/> result: picking
/// a `.ser` file re-scans its `raw`/`processed` subfolders (see
/// <see cref="ProcessingLocations.GetImagesFolder"/> - matches real JSolex's own per-kind subfolder
/// layout exactly, see <see cref="GeneratedImageKindExtensions.GetDirectoryKind"/>) for whatever PNGs
/// are already sitting there, so a file processed in an earlier session (by SolScan or by JSolex
/// itself) can be revisited without re-running Process. Each preview is decoded
/// straight back off disk (WPF's own <see cref="PngBitmapDecoder"/>, converted to
/// <see cref="PixelFormats.Gray16"/>) and auto-stretched via <see cref="FramePreview"/> - the exact
/// same histogram/auto-stretch math the live Capture preview already uses - since the saved PNGs are
/// deliberately unstretched real sensor-scale values (see <see cref="ShgProcessor"/>'s own doc
/// comment) that would look flat/washed shown directly.
/// </summary>
public partial class ProcessViewModel : ObservableObject
{
    private readonly Func<ISerReader> _serReaderFactory;
    private readonly ICaptureMetadataStore _metadataStore;
    private readonly IShgProcessor _shgProcessor;
    private readonly IProcessParamsStore _processParamsStore;
    private CancellationTokenSource? _processingCts;

    /// <summary>One entry in <see cref="AvailableProcessedImages"/> - a PNG found in the output
    /// folder, with a display label derived from its filename (e.g. "raw.png" -> "Raw"). Public, not
    /// private - the [ObservableProperty] source generator emits a public property of this type,
    /// which can't be less accessible than the property itself; still only reachable as the nested
    /// ProcessViewModel.ProcessedImageFile from outside this class.</summary>
    public sealed record ProcessedImageFile(string Label, string FilePath);

    [ObservableProperty]
    private string statusText = "Pick a .ser file to inspect its header and equipment metadata.";

    [ObservableProperty]
    private string? selectedFilePath;

    [ObservableProperty]
    private string? fileInfoText;

    [ObservableProperty]
    private string? equipmentInfoText;

    [ObservableProperty]
    private string? outputFolderText;

    [ObservableProperty]
    private string? captureDetailsText;

    /// <summary>True while <see cref="ProcessAsync"/> is running - gates re-entry and disables
    /// Browse, matching <see cref="CaptureViewModel"/>'s own <c>IsFindingSun</c> busy-flag shape.</summary>
    [ObservableProperty]
    private bool isProcessing;

    /// <summary>Whatever `.png` files are actually sitting in the current file's `raw`/`processed`
    /// subfolders right now - see the class doc comment for why this is disk-based rather than tied
    /// to the most recent <see cref="ProcessAsync"/> run.</summary>
    [ObservableProperty]
    private ObservableCollection<ProcessedImageFile> availableProcessedImages = [];

    [ObservableProperty]
    private ProcessedImageFile? selectedProcessedImage;

    /// <summary>An auto-stretched, downsampled preview of <see cref="SelectedProcessedImage"/> - see
    /// <see cref="UpdateProcessedImagePreview"/>.</summary>
    [ObservableProperty]
    private WriteableBitmap? processedImagePreview;

    public ProcessViewModel(
        Func<ISerReader> serReaderFactory,
        ICaptureMetadataStore metadataStore,
        IShgProcessor shgProcessor,
        IProcessParamsStore processParamsStore)
    {
        _serReaderFactory = serReaderFactory;
        _metadataStore = metadataStore;
        _shgProcessor = shgProcessor;
        _processParamsStore = processParamsStore;
    }

    private bool CanBrowse() => !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private void BrowseForSerFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a .ser file to process",
            Filter = "SER video files (*.ser)|*.ser|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(CaptureLocations.DefaultCapturesRootFolder)
                ? CaptureLocations.DefaultCapturesRootFolder
                : null,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SelectedFilePath = dialog.FileName;
        LoadSelectedFile();
    }

    private void LoadSelectedFile()
    {
        if (SelectedFilePath is null)
        {
            return;
        }

        FileInfoText = null;
        EquipmentInfoText = null;
        OutputFolderText = null;
        CaptureDetailsText = null;

        try
        {
            using var reader = _serReaderFactory();
            reader.Open(SelectedFilePath);
            var header = reader.Header;
            FileInfoText = $"{header.Width} x {header.Height}, {header.PixelDepth}-bit, "
                + $"{header.FrameCount} frame(s), recorded {header.DateTimeUtc:yyyy-MM-dd HH:mm:ss} UTC";

            var metadata = _metadataStore.TryRead(SelectedFilePath);
            EquipmentInfoText = metadata is null
                ? "No equipment metadata found alongside this file."
                : $"SHG: {metadata.Spectrograph?.Label ?? "(none)"} · "
                    + $"Telescope: {metadata.Telescope?.Label ?? "(none)"} · "
                    + $"Camera: {metadata.Camera?.Label ?? "(none)"}";
            CaptureDetailsText = BuildCaptureDetailsText(metadata);

            OutputFolderText = $"Output folder: {ProcessingLocations.GetOutputFolder(SelectedFilePath)}";
            RefreshProcessedImages(SelectedFilePath);

            StatusText = "Loaded. Click Process to run spectral-line detection and reconstruction.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusText = $"Failed to read '{SelectedFilePath}': {ex.Message}";
        }
    }

    private bool CanProcess() => SelectedFilePath is not null && !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanProcess))]
    private async Task ProcessAsync()
    {
        if (SelectedFilePath is null)
        {
            return;
        }

        _processingCts = new CancellationTokenSource();
        try
        {
            IsProcessing = true;

            var processParams = _processParamsStore.Load();
            var progress = new Progress<string>(s => StatusText = s);
            var result = await _shgProcessor.ProcessAsync(SelectedFilePath, processParams, progress, _processingCts.Token);

            // Each kind goes in its own raw/processed subfolder, matching real JSolex's own layout -
            // see GeneratedImageKindExtensions.GetDirectoryKind.
            var createdFolders = new HashSet<string>();
            foreach (var image in result.Images)
            {
                var folder = ProcessingLocations.GetImagesFolder(SelectedFilePath, image.Kind.GetDirectoryKind());
                if (createdFolders.Add(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                SavePng(Path.Combine(folder, OutputFileName(image.Kind)), image);
            }

            // Disk-based, so this also picks up the files just written above - see the class doc
            // comment for why the dropdown isn't just populated from result.Images directly.
            RefreshProcessedImages(SelectedFilePath);

            StatusText = BuildResultSummary(result, ProcessingLocations.GetOutputFolder(SelectedFilePath));
        }
        catch (OperationCanceledException)
        {
            StatusText = "Processing cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Processing failed: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            _processingCts?.Dispose();
            _processingCts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsProcessing))]
    private void CancelProcessing() => _processingCts?.Cancel();

    partial void OnIsProcessingChanged(bool value)
    {
        ProcessCommand.NotifyCanExecuteChanged();
        BrowseForSerFileCommand.NotifyCanExecuteChanged();
        CancelProcessingCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedFilePathChanged(string? value) => ProcessCommand.NotifyCanExecuteChanged();

    /// <summary>Re-scans <paramref name="serFilePath"/>'s `raw` and `processed` subfolders (see
    /// <see cref="ProcessingLocations.GetImagesFolder"/>) for `.png` files and repopulates the
    /// dropdown - called both when a file is picked (so already-processed output shows up
    /// immediately) and after a Process run finishes. Keeps the previously-selected file selected if
    /// it's still present; otherwise prefers "raw.png", else whatever sorts first.</summary>
    private void RefreshProcessedImages(string serFilePath)
    {
        var previousPath = SelectedProcessedImage?.FilePath;

        var files = new List<string>();
        foreach (var directoryKind in new[] { DirectoryKind.Raw, DirectoryKind.Processed })
        {
            var folder = ProcessingLocations.GetImagesFolder(serFilePath, directoryKind);
            if (Directory.Exists(folder))
            {
                files.AddRange(Directory.EnumerateFiles(folder, "*.png"));
            }
        }

        AvailableProcessedImages = new ObservableCollection<ProcessedImageFile>(
            files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => new ProcessedImageFile(LabelFor(f), f)));

        var defaultFile = AvailableProcessedImages.FirstOrDefault(f => f.FilePath == previousPath)
            ?? AvailableProcessedImages.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f.FilePath), "raw", StringComparison.OrdinalIgnoreCase))
            ?? AvailableProcessedImages.FirstOrDefault();

        if (SelectedProcessedImage == defaultFile)
        {
            // Same file as before (e.g. re-processing overwrote it) - the property setter below
            // wouldn't fire since the value hasn't changed, but the file's content may have, so
            // render explicitly rather than leaving a stale preview on screen.
            UpdateProcessedImagePreview();
        }
        else
        {
            SelectedProcessedImage = defaultFile; // triggers OnSelectedProcessedImageChanged
        }
    }

    private static string LabelFor(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
    }

    partial void OnSelectedProcessedImageChanged(ProcessedImageFile? value) => UpdateProcessedImagePreview();

    /// <summary>Decodes <see cref="SelectedProcessedImage"/> straight back off disk, auto-stretches
    /// it (see <see cref="FramePreview.ComputeAutoStretch"/>) and downsamples it (see
    /// <see cref="FramePreview.Stretch"/>) into <see cref="ProcessedImagePreview"/> - same
    /// WriteableBitmap-reuse pattern as <c>CaptureViewModel.RenderPreview</c>.</summary>
    private void UpdateProcessedImagePreview()
    {
        if (SelectedProcessedImage is not { } selected)
        {
            ProcessedImagePreview = null;
            return;
        }

        try
        {
            var frame = LoadCameraFrameFromPng(selected.FilePath);
            var histogram = FramePreview.ComputeHistogram(frame);
            var (blackPoint, whitePoint) = FramePreview.ComputeAutoStretch(histogram);
            var (pixels, width, height) = FramePreview.Stretch(frame, blackPoint, whitePoint);

            if (ProcessedImagePreview is null || ProcessedImagePreview.PixelWidth != width || ProcessedImagePreview.PixelHeight != height)
            {
                ProcessedImagePreview = new WriteableBitmap(width, height, 96, 96, PixelFormats.Gray8, palette: null);
            }

            ProcessedImagePreview.WritePixels(new Int32Rect(0, 0, width, height), pixels, width, offset: 0);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            ProcessedImagePreview = null;
            StatusText = $"Failed to load '{selected.FilePath}': {ex.Message}";
        }
    }

    /// <summary>Reads a PNG back as a 16-bit-grayscale <see cref="CameraFrame"/> - the shape
    /// <see cref="FramePreview"/> expects. <c>BitmapCacheOption.OnLoad</c> so the decoded pixels
    /// don't depend on the file stream staying open past this method (a well-known WPF imaging
    /// gotcha with the default <c>OnDemand</c> caching).</summary>
    private static CameraFrame LoadCameraFrameFromPng(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Gray16, null, 0);

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 2;
        var data = new byte[height * stride];
        converted.CopyPixels(data, stride, 0);

        return new CameraFrame(data, width, height, 16, DateTime.UtcNow);
    }

    private static string OutputFileName(GeneratedImageKind kind) => kind switch
    {
        GeneratedImageKind.Raw => "raw.png",
        GeneratedImageKind.Reconstruction => "reconstruction.png",
        GeneratedImageKind.Continuum => "continuum.png",
        _ => $"{kind.ToString().ToLowerInvariant()}.png",
    };

    private static string BuildResultSummary(ShgProcessingResult result, string outputFolder)
    {
        var summary = $"Wrote {result.Images.Count} image(s) under {outputFolder} (raw/processed subfolders).";
        if (result.DetectedLinePolynomial is { } polynomial)
        {
            summary += $" Line curve: y = {polynomial.A:F6}x² + {polynomial.B:F4}x + {polynomial.C:F2}.";
        }

        if (result.SkippedKinds.Count > 0)
        {
            summary += $" Not yet implemented (needs geometry/ellipse fitting): {string.Join(", ", result.SkippedKinds)}.";
        }

        return summary;
    }

    /// <summary>Saves a 16-bit grayscale <see cref="ProcessedImage"/> as PNG via WPF's own
    /// <see cref="PngBitmapEncoder"/>/<see cref="PixelFormats.Gray16"/> - see
    /// <see cref="ShgProcessor"/>'s own doc comment for why this lives here rather than in
    /// SolScan.Processing (which has no file IO, and no WPF dependency, at all).</summary>
    private static void SavePng(string path, ProcessedImage image)
    {
        var pixels = new ushort[image.Width * image.Height];
        var index = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                pixels[index++] = image.Pixels[y, x];
            }
        }

        var stride = image.Width * 2; // 2 bytes/pixel for Gray16
        var bitmapSource = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Gray16, null, pixels, stride);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Summarises whichever of <see cref="CaptureMetadata"/>'s newer fields (camera settings
    /// actually used, mount pointing at recording start, studied spectral line) are present -
    /// separate from <see cref="EquipmentInfoText"/> so that stays focused on the SHG/telescope/
    /// camera it always showed. Returns null (nothing shown) rather than an empty line when a
    /// recording has none of these - e.g. a sidecar written before <c>CaptureMetadata</c> grew them,
    /// or the mount wasn't connected at recording start.</summary>
    private static string? BuildCaptureDetailsText(CaptureMetadata? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        var parts = new List<string>();
        if (metadata.CameraSettingsUsed is { } settings)
        {
            parts.Add($"Gain {settings.Gain:F1}, Exposure {settings.ExposureMicroseconds:F0}µs, "
                + $"{settings.OutputFormat}, Bin {settings.Binning}");
        }
        if (metadata.MountPointing is { } pointing)
        {
            parts.Add($"RA {pointing.RightAscensionHours:F3}h Dec {pointing.DeclinationDeg:F2}°");
        }
        if (metadata.StudiedRay is { } ray)
        {
            parts.Add($"Line: {ray.Label}");
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }
}
