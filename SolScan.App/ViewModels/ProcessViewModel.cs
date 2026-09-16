using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SolScan.App.ViewModels.Processing;
using SolScan.App.Views;
using SolScan.Core.Camera;
using SolScan.Core.Capture;
using SolScan.Core.Processing;
using SolScan.Processing.Shg;

namespace SolScan.App.ViewModels;

/// <summary>
/// Process stage: pick a finished SER capture, inspect its header/equipment metadata, and run it
/// through <see cref="IShgProcessor"/> - real spectral-line-curvature detection, reconstruction,
/// disk-edge ellipse fitting/geometry correction, and (for
/// <see cref="GeneratedImageKind.GeometryCorrectedProcessed"/>) every <see cref="ContrastEnhancementMode"/>
/// (see <see cref="ShgProcessor"/>'s own doc comment), producing every real output image SolScan
/// declares (see <see cref="GeneratedImageKind"/>). Processing is manual (click Process)
/// rather than automatic-on-capture-finish
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
///
/// <see cref="ProcessParameters"/>/<see cref="ImageEnhancement"/>/<see cref="ImageSelection"/> - the
/// three views that used to be Options tabs editing one shared <see cref="ProcessParams"/> via an
/// explicit Save button (see <c>OptionsViewModel</c>'s own former doc comment) - now live directly on
/// this view instead, as a dockable right-hand panel (<see cref="IsProcessOptionsPanelExpanded"/>) of
/// Expanders (<see cref="IsProcessParametersExpanded"/>/<see cref="IsImageEnhancementExpanded"/>/
/// <see cref="IsImageSelectionExpanded"/>, each persisted the same way <c>CaptureViewModel</c>'s own
/// Expanders are). Edits auto-save instead: any of the three child view models raising
/// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> (re)starts a 300ms
/// debounce timer (same rapid-fire-write rationale as <c>CaptureViewModel.PersistSettingsIfConnected</c>'s
/// own timer) that rebuilds one <see cref="ProcessParams"/> from all three and saves it - the exact
/// reassembly <c>OptionsViewModel.Save</c> used to do on a button click, just automatic now.
/// <see cref="ProcessAsync"/> flushes that timer and builds its own fresh <see cref="ProcessParams"/>
/// from the live view models rather than re-<see cref="IProcessParamsStore.Load"/>ing, so clicking
/// Process within the debounce window can never run against stale, pre-edit values.
/// </summary>
public partial class ProcessViewModel : ObservableObject
{
    private readonly Func<ISerReader> _serReaderFactory;
    private readonly ICaptureMetadataStore _metadataStore;
    private readonly IShgProcessor _shgProcessor;
    private readonly IProcessParamsStore _processParamsStore;
    private readonly IAppSettingsStore _appSettingsStore;
    private readonly Func<SerCropWindow> _serCropWindowFactory;
    private readonly StatusBarViewModel _statusBar;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _processingCts;
    private SerCropWindow? _serCropWindow;

    // Debounces the auto-save triggered by ProcessParameters/ImageEnhancement/ImageSelection's own
    // PropertyChanged events - see this class's own doc comment for why.
    private DispatcherTimer? _persistProcessParamsDebounceTimer;

    // Debounces OnProcessOptionsPanelWidthChanged - see that method's own doc comment for why.
    private DispatcherTimer? _persistPanelWidthDebounceTimer;

    /// <summary>One entry in <see cref="AvailableProcessedImages"/> - a PNG found in the output
    /// folder, with a display label derived from its filename (e.g. "raw.png" -> "Raw"). Public, not
    /// private - the [ObservableProperty] source generator emits a public property of this type,
    /// which can't be less accessible than the property itself; still only reachable as the nested
    /// ProcessViewModel.ProcessedImageFile from outside this class.</summary>
    public sealed record ProcessedImageFile(string Label, string FilePath);

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

    // The three fields below back the "Processing Results" panel (ProcessView.xaml's right-hand
    // column) - astro4j's own two-part "detected line"/"detected geometry" info view (see
    // ShgProcessingResult's own doc comment) plus an overall image-count/output-folder summary,
    // previously only ever shown as one transient status-bar line (BuildResultSummary, still used
    // for that). Unlike AvailableProcessedImages (deliberately disk-based - see this class's own
    // doc comment), these are never persisted anywhere, so there's no way to recover them for a
    // file processed in an earlier session without running Process again - reset to a "not yet
    // processed" placeholder whenever a different file is selected, and only ever updated here on a
    // *successful* ProcessAsync completion (left as-is, not blanked, on cancel/failure, so a
    // previous successful run's numbers stay visible rather than being wiped by an unrelated error).
    [ObservableProperty]
    private string detectedLineText = NoLineDetectedYetText;

    [ObservableProperty]
    private string detectedGeometryText = NoGeometryDetectedYetText;

    [ObservableProperty]
    private string resultImagesText = NoImagesYetText;

    private const string NoLineDetectedYetText = "No spectral line curve detected yet.";
    private const string NoGeometryDetectedYetText = "No geometry correction detected yet.";
    private const string NoImagesYetText = "Run Process to generate output images.";

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

    public ProcessParametersViewModel ProcessParameters { get; }
    public ImageEnhancementViewModel ImageEnhancement { get; }
    public ImageSelectionViewModel ImageSelection { get; }

    /// <summary>Whether the right-hand parameters panel is docked open or collapsed to give the image
    /// preview the full window width.</summary>
    [ObservableProperty]
    private bool isProcessOptionsPanelExpanded;

    /// <summary>Whether the panel above is "pinned" - VS-tool-window style - into a real, resizable
    /// docked column (ProcessView.xaml's own code-behind, <c>UpdatePanelDockState</c>) instead of
    /// shown as md:DrawerHost's default floating overlay. Off by default (today's overlay-only
    /// behavior unchanged) - see <see cref="IsProcessDrawerOpen"/>/<see cref="IsProcessOptionsPanelDocked"/>
    /// for how this and <see cref="IsProcessOptionsPanelExpanded"/> combine to pick one or the other.
    /// Mirrors <c>CaptureViewModel</c>'s own identically-named members exactly.</summary>
    [ObservableProperty]
    private bool isProcessOptionsPanelPinned;

    /// <summary>The docked column's width in pixels while pinned - see
    /// <c>CaptureViewModel.CaptureOptionsPanelWidth</c>'s own doc comment, same idea.</summary>
    [ObservableProperty]
    private double processOptionsPanelWidth = 340;

    /// <summary>See <c>CaptureViewModel.IsCaptureDrawerOpen</c>'s own doc comment - identical
    /// reasoning, just for this view's panel.</summary>
    public bool IsProcessDrawerOpen
    {
        get => IsProcessOptionsPanelExpanded && !IsProcessOptionsPanelPinned;
        set => IsProcessOptionsPanelExpanded = value;
    }

    /// <summary>See <c>CaptureViewModel.IsCaptureOptionsPanelDocked</c>'s own doc comment - identical
    /// reasoning, just for this view's panel.</summary>
    public bool IsProcessOptionsPanelDocked => IsProcessOptionsPanelExpanded && IsProcessOptionsPanelPinned;

    [ObservableProperty]
    private bool isProcessParametersExpanded;

    [ObservableProperty]
    private bool isImageEnhancementExpanded;

    [ObservableProperty]
    private bool isImageSelectionExpanded;

    public ProcessViewModel(
        Func<ISerReader> serReaderFactory,
        ICaptureMetadataStore metadataStore,
        IShgProcessor shgProcessor,
        IProcessParamsStore processParamsStore,
        IAppSettingsStore appSettingsStore,
        Func<SerCropWindow> serCropWindowFactory,
        StatusBarViewModel statusBarViewModel)
    {
        _serReaderFactory = serReaderFactory;
        _metadataStore = metadataStore;
        _shgProcessor = shgProcessor;
        _processParamsStore = processParamsStore;
        _appSettingsStore = appSettingsStore;
        _serCropWindowFactory = serCropWindowFactory;
        _statusBar = statusBarViewModel;
        _dispatcher = Dispatcher.CurrentDispatcher;

        var processParams = processParamsStore.Load();
        ProcessParameters = new ProcessParametersViewModel(processParams);
        ImageEnhancement = new ImageEnhancementViewModel(processParams);
        ImageSelection = new ImageSelectionViewModel(processParams);
        ProcessParameters.PropertyChanged += (_, _) => SchedulePersistProcessParams();
        ImageEnhancement.PropertyChanged += (_, _) => SchedulePersistProcessParams();
        ImageSelection.PropertyChanged += (_, _) => SchedulePersistProcessParams();

        var appSettings = appSettingsStore.Load();
        isProcessOptionsPanelExpanded = appSettings.ProcessOptionsPanelExpanded;
        isProcessOptionsPanelPinned = appSettings.ProcessOptionsPanelPinned;
        processOptionsPanelWidth = appSettings.ProcessOptionsPanelWidth;
        isProcessParametersExpanded = appSettings.ProcessParametersExpanded;
        isImageEnhancementExpanded = appSettings.ProcessImageEnhancementExpanded;
        isImageSelectionExpanded = appSettings.ProcessImageSelectionExpanded;

        SetStatus("Pick a .ser file to inspect its header and equipment metadata.");
    }

    /// <summary>Narrates progress/results into the shared <see cref="StatusBarViewModel"/> (see its
    /// own doc comment) rather than a status TextBlock local to this view - so it stays visible
    /// regardless of which stage is currently on screen, matching Capture/Mount's own status-bar
    /// fields.</summary>
    private void SetStatus(string message) => _statusBar.ProcessStatusText = $"Process: {message}";

    partial void OnIsProcessOptionsPanelExpandedChanged(bool value)
    {
        PersistAppSetting(s => s with { ProcessOptionsPanelExpanded = value });
        OnPropertyChanged(nameof(IsProcessDrawerOpen));
        OnPropertyChanged(nameof(IsProcessOptionsPanelDocked));
    }

    partial void OnIsProcessOptionsPanelPinnedChanged(bool value)
    {
        PersistAppSetting(s => s with { ProcessOptionsPanelPinned = value });
        OnPropertyChanged(nameof(IsProcessDrawerOpen));
        OnPropertyChanged(nameof(IsProcessOptionsPanelDocked));
    }

    /// <summary>Debounced the same way <see cref="SchedulePersistProcessParams"/> is - a GridSplitter
    /// drag raises this on every pixel of movement (see ProcessView.xaml.cs's
    /// <c>OptionsPanelHost.SizeChanged</c> handler).</summary>
    partial void OnProcessOptionsPanelWidthChanged(double value)
    {
        _persistPanelWidthDebounceTimer ??= CreatePersistPanelWidthDebounceTimer();
        _persistPanelWidthDebounceTimer.Stop();
        _persistPanelWidthDebounceTimer.Start();
    }

    private DispatcherTimer CreatePersistPanelWidthDebounceTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            PersistAppSetting(s => s with { ProcessOptionsPanelWidth = ProcessOptionsPanelWidth });
        };
        return timer;
    }

    partial void OnIsProcessParametersExpandedChanged(bool value) =>
        PersistAppSetting(s => s with { ProcessParametersExpanded = value });

    partial void OnIsImageEnhancementExpandedChanged(bool value) =>
        PersistAppSetting(s => s with { ProcessImageEnhancementExpanded = value });

    partial void OnIsImageSelectionExpandedChanged(bool value) =>
        PersistAppSetting(s => s with { ProcessImageSelectionExpanded = value });

    private void PersistAppSetting(Func<AppSettings, AppSettings> update) =>
        _appSettingsStore.Save(update(_appSettingsStore.Load()));

    /// <summary>(Re)starts the 300ms debounce timer that rebuilds and saves one <see cref="ProcessParams"/>
    /// from <see cref="ProcessParameters"/>/<see cref="ImageEnhancement"/>/<see cref="ImageSelection"/> -
    /// see this class's own doc comment for why this is debounced rather than saved on every change.</summary>
    private void SchedulePersistProcessParams()
    {
        _persistProcessParamsDebounceTimer ??= CreatePersistProcessParamsDebounceTimer();
        _persistProcessParamsDebounceTimer.Stop();
        _persistProcessParamsDebounceTimer.Start();
    }

    private DispatcherTimer CreatePersistProcessParamsDebounceTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _processParamsStore.Save(BuildCurrentProcessParams());
        };
        return timer;
    }

    /// <summary>Stops any pending debounced save and persists <paramref name="current"/> immediately -
    /// called right before a Process run starts, so the sidecar/settings file on disk always matches
    /// exactly what's about to be processed, never a value from before the debounce window elapsed.</summary>
    private void FlushPendingProcessParams(ProcessParams current)
    {
        _persistProcessParamsDebounceTimer?.Stop();
        _processParamsStore.Save(current);
    }

    /// <summary>Reassembles one <see cref="ProcessParams"/> from the three child view models' current
    /// (in-memory, not necessarily yet saved) values - the same reassembly <c>OptionsViewModel.Save</c>
    /// used to do on a button click.</summary>
    private ProcessParams BuildCurrentProcessParams() => new(
        ImageSelection.ToRequestedImages(),
        ProcessParameters.ToSpectrumParams(),
        ProcessParameters.ToGeometryParams(),
        ImageEnhancement.ToContrastEnhancement(),
        ImageEnhancement.ToClaheParams(),
        ImageEnhancement.ToClahe2Params(),
        ImageEnhancement.ToAutoStretchParams());

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

    /// <summary>Opens the pop-out, modeless "Crop SER" window (see Views/SerCropWindow.xaml) - for
    /// recordings made before a hardware ROI was set up, so their full-sensor-height frames can be
    /// trimmed down to roughly what a proper ROI capture would have produced. Brings the existing
    /// window to front instead of opening a second if one's already open - same reasoning as
    /// CaptureViewModel.OpenHandControl's own doc comment.</summary>
    [RelayCommand]
    private void OpenSerCrop()
    {
        if (_serCropWindow is not null)
        {
            _serCropWindow.Activate();
            return;
        }

        _serCropWindow = _serCropWindowFactory();
        _serCropWindow.Closed += (_, _) => _serCropWindow = null;
        _serCropWindow.Show();
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

        // A newly-selected file's own results (if any) haven't been generated this session - see
        // the three properties' own doc comment for why there's no way to recover them from disk.
        DetectedLineText = NoLineDetectedYetText;
        DetectedGeometryText = NoGeometryDetectedYetText;
        ResultImagesText = NoImagesYetText;

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

            SetStatus("Loaded. Click Process to run spectral-line detection and reconstruction.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SetStatus($"Failed to read '{SelectedFilePath}': {ex.Message}");
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

            // Built from the live child view models, not reloaded from disk - see this class's own
            // doc comment for why (the debounce timer may not have flushed a very recent edit yet).
            var processParams = BuildCurrentProcessParams();
            FlushPendingProcessParams(processParams);
            var progress = new Progress<string>(SetStatus);
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

            foreach (var colorImage in result.ColorImages ?? [])
            {
                var folder = ProcessingLocations.GetImagesFolder(SelectedFilePath, colorImage.Kind.GetDirectoryKind());
                if (createdFolders.Add(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                SaveColorPng(Path.Combine(folder, OutputFileName(colorImage.Kind)), colorImage);
            }

            // Disk-based, so this also picks up the files just written above - see the class doc
            // comment for why the dropdown isn't just populated from result.Images directly.
            // preferColorized: right after a run that actually produced one, show it by default
            // rather than whatever was previously selected (or "raw.png") - see RefreshProcessedImages'
            // own doc comment.
            RefreshProcessedImages(SelectedFilePath, preferColorized: result.ColorImages is { Count: > 0 });

            var outputFolder = ProcessingLocations.GetOutputFolder(SelectedFilePath);
            SetStatus(BuildResultSummary(result, outputFolder));
            UpdateResultInfoPanel(result, outputFolder);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Processing cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus($"Processing failed: {ex.Message}");
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
    /// immediately, <paramref name="preferColorized"/> false) and after a Process run finishes
    /// (<paramref name="preferColorized"/> true when that run actually produced one - see
    /// <see cref="ProcessAsync"/>'s own call site). When true, "colorized.png" wins over even the
    /// previously-selected file - right after generating it, that's the result worth looking at, not
    /// whatever happened to be on screen before. Otherwise keeps the previously-selected file selected
    /// if it's still present; otherwise prefers "raw.png", else whatever sorts first.</summary>
    private void RefreshProcessedImages(string serFilePath, bool preferColorized = false)
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

        var defaultFile = (preferColorized ? AvailableProcessedImages.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f.FilePath), "colorized", StringComparison.OrdinalIgnoreCase)) : null)
            ?? AvailableProcessedImages.FirstOrDefault(f => f.FilePath == previousPath)
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

    /// <summary>Decodes <see cref="SelectedProcessedImage"/> straight back off disk into
    /// <see cref="ProcessedImagePreview"/> - a mono PNG (e.g. <c>raw.png</c>) is auto-stretched (see
    /// <see cref="FramePreview.ComputeAutoStretch"/>) and downsampled (see <see cref="FramePreview.Stretch"/>)
    /// the same way the live Capture preview is; a colour PNG (currently just <c>colorized.png</c>) is
    /// shown as-is - the colorization pipeline already stretched/tinted it, so re-auto-stretching would
    /// just wash out its own colour curve. Same WriteableBitmap-reuse pattern as
    /// <c>CaptureViewModel.RenderPreview</c>, keyed on pixel format too now that this can produce either
    /// a <see cref="PixelFormats.Gray8"/> or a <see cref="PixelFormats.Bgra32"/> bitmap.</summary>
    private void UpdateProcessedImagePreview()
    {
        if (SelectedProcessedImage is not { } selected)
        {
            ProcessedImagePreview = null;
            return;
        }

        try
        {
            if (IsColorPng(selected.FilePath))
            {
                var (pixels, width, height) = LoadColorPreviewPixels(selected.FilePath);
                EnsurePreviewBitmap(width, height, PixelFormats.Bgra32);
                ProcessedImagePreview!.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, offset: 0);
            }
            else
            {
                var frame = LoadCameraFrameFromPng(selected.FilePath);
                var histogram = FramePreview.ComputeHistogram(frame);
                var (blackPoint, whitePoint) = FramePreview.ComputeAutoStretch(histogram);
                var (pixels, width, height) = FramePreview.Stretch(frame, blackPoint, whitePoint);
                EnsurePreviewBitmap(width, height, PixelFormats.Gray8);
                ProcessedImagePreview!.WritePixels(new Int32Rect(0, 0, width, height), pixels, width, offset: 0);
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            ProcessedImagePreview = null;
            SetStatus($"Failed to load '{selected.FilePath}': {ex.Message}");
        }
    }

    private void EnsurePreviewBitmap(int width, int height, PixelFormat format)
    {
        if (ProcessedImagePreview is null || ProcessedImagePreview.PixelWidth != width || ProcessedImagePreview.PixelHeight != height || ProcessedImagePreview.Format != format)
        {
            ProcessedImagePreview = new WriteableBitmap(width, height, 96, 96, format, palette: null);
        }
    }

    /// <summary>Whether <paramref name="path"/> is a colour PNG (currently just <c>colorized.png</c>)
    /// rather than one of the mono outputs - peeked from the decoded frame's own pixel format, without
    /// decoding pixel data (<see cref="BitmapCacheOption.None"/>).</summary>
    private static bool IsColorPng(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.None);
        var format = decoder.Frames[0].Format;
        return format != PixelFormats.Gray16 && format != PixelFormats.Gray8 && format != PixelFormats.BlackWhite;
    }

    /// <summary>Reads a colour PNG back as flat Bgra32 bytes, ready for <see cref="WriteableBitmap.WritePixels(Int32Rect,Array,int,int)"/> -
    /// no auto-stretch/downsample (see <see cref="UpdateProcessedImagePreview"/>'s own doc comment for why).</summary>
    private static (byte[] Pixels, int Width, int Height) LoadColorPreviewPixels(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var data = new byte[height * stride];
        converted.CopyPixels(data, stride, 0);

        return (data, width, height);
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
        GeneratedImageKind.GeometryCorrected => "geometry-corrected.png",
        GeneratedImageKind.GeometryCorrectedProcessed => "geometry-corrected-processed.png",
        GeneratedImageKind.Colorized => "colorized.png",
        GeneratedImageKind.VirtualEclipse => "virtual-eclipse.png",
        _ => $"{kind.ToString().ToLowerInvariant()}.png",
    };

    /// <summary>astro4j's own "detected line" info-panel figure, formatted as one sentence - null if
    /// nothing needed reconstruction. Shared between <see cref="BuildResultSummary"/> (the transient
    /// status-bar line, which omits this fragment entirely when null) and
    /// <see cref="UpdateResultInfoPanel"/> (the persistent panel, which shows
    /// <see cref="NoLineDetectedYetText"/> instead).</summary>
    private static string? FormatDetectedLine(ShgProcessingResult result) =>
        result.DetectedLinePolynomial is { } polynomial
            ? $"Line curve: y = {polynomial.A:F6}x² + {polynomial.B:F4}x + {polynomial.C:F2}."
            : null;

    /// <summary>astro4j's own "detected geometry" info-panel figure - the other half of that same
    /// panel alongside <see cref="FormatDetectedLine"/>, null under the same "wasn't requested"
    /// condition.</summary>
    private static string? FormatDetectedGeometry(ShgProcessingResult result) =>
        result.DetectedTiltDegrees is { } tiltDegrees && result.DetectedXyRatio is { } xyRatio
            ? $"Disk tilt: {tiltDegrees:F2}°, X/Y ratio: {xyRatio:F3}."
            : null;

    private static string BuildResultSummary(ShgProcessingResult result, string outputFolder)
    {
        var summary = $"Wrote {result.Images.Count} image(s) under {outputFolder} (raw/processed subfolders).";
        if (FormatDetectedLine(result) is { } line)
        {
            summary += $" {line}";
        }

        if (FormatDetectedGeometry(result) is { } geometry)
        {
            summary += $" {geometry}";
        }

        if (result.SkippedKinds.Count > 0)
        {
            // Currently always empty in practice - see ShgProcessingResult.SkippedKinds' own doc
            // comment - but kept as a generic "reported explicitly rather than silently dropped"
            // mechanism for whenever a future kind needs it.
            summary += $" Not yet implemented: {string.Join(", ", result.SkippedKinds)}.";
        }

        return summary;
    }

    /// <summary>Populates the "Processing Results" panel's three properties from a just-completed
    /// <paramref name="result"/> - see those properties' own doc comment for why this only ever runs
    /// on success.</summary>
    private void UpdateResultInfoPanel(ShgProcessingResult result, string outputFolder)
    {
        DetectedLineText = FormatDetectedLine(result) ?? NoLineDetectedYetText;
        DetectedGeometryText = FormatDetectedGeometry(result) ?? NoGeometryDetectedYetText;
        ResultImagesText = result.SkippedKinds.Count > 0
            ? $"Wrote {result.Images.Count} image(s) under {outputFolder} (raw/processed subfolders). "
                + $"Not yet implemented: {string.Join(", ", result.SkippedKinds)}."
            : $"Wrote {result.Images.Count} image(s) under {outputFolder} (raw/processed subfolders).";
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

    /// <summary>Saves a 16-bit-per-channel <see cref="ProcessedColorImage"/> (currently only
    /// <see cref="GeneratedImageKind.Colorized"/>) as PNG via <see cref="PixelFormats.Rgb48"/> - the
    /// colour counterpart to <see cref="SavePng"/>.</summary>
    private static void SaveColorPng(string path, ProcessedColorImage image)
    {
        var pixels = new ushort[image.Width * image.Height * 3];
        var index = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                pixels[index++] = image.R[y, x];
                pixels[index++] = image.G[y, x];
                pixels[index++] = image.B[y, x];
            }
        }

        var stride = image.Width * 6; // 3 channels * 2 bytes/channel for Rgb48
        var bitmapSource = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Rgb48, null, pixels, stride);

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
