using System.IO;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SolScan.Core.Capture;
using SolScan.Processing.Capture;

namespace SolScan.App.ViewModels;

/// <summary>
/// Backs the pop-out, modeless "Crop SER" window (see Views/SerCropWindow.xaml) - for recordings made
/// before a hardware ROI was set up (see <see cref="SolScan.Core.Camera.ICameraDevice.SetOutputFormatAsync"/>'s
/// own centred-ROI note), so the full-sensor-height frames those captures carry can still be trimmed
/// down to roughly what a proper ROI capture would have produced, before being handed to
/// <see cref="SolScan.Processing.Shg.IShgProcessor"/>. Same window-per-open/Closed-clears-the-reference
/// shape as <c>CaptureViewModel.OpenHandControl</c>, but launched from <c>ProcessViewModel</c> instead -
/// this is a Process-stage concern (fixing up a file before processing it), not a live-capture one.
/// </summary>
public partial class SerCropViewModel : ObservableObject
{
    private readonly Func<ISerReader> _serReaderFactory;
    private readonly ISerCropper _serCropper;
    private readonly ICaptureMetadataStore _metadataStore;
    private CancellationTokenSource? _cropCts;

    private SerHeader? _sourceHeader;

    [ObservableProperty]
    private string? sourceFilePath;

    [ObservableProperty]
    private string? sourceInfoText;

    [ObservableProperty]
    private string? outputFilePath;

    /// <summary>How much of the source height to keep, as a percentage (0, 100] - <see cref="SerCropper.ComputeCroppedHeight"/>
    /// resolves this the same way both here (for the live preview text) and inside
    /// <see cref="ISerCropper.CropAsync"/> itself, so they can't disagree.</summary>
    [ObservableProperty]
    private double heightPercent = 20.0;

    [ObservableProperty]
    private string? croppedHeightPreviewText;

    [ObservableProperty]
    private string statusText = "Pick a full-frame .ser file to crop.";

    /// <summary>True while <see cref="CropAsync"/> is running - gates re-entry and disables Browse/the
    /// height field, matching <c>ProcessViewModel</c>'s own <c>IsProcessing</c> busy-flag shape.</summary>
    [ObservableProperty]
    private bool isCropping;

    public SerCropViewModel(Func<ISerReader> serReaderFactory, ISerCropper serCropper, ICaptureMetadataStore metadataStore)
    {
        _serReaderFactory = serReaderFactory;
        _serCropper = serCropper;
        _metadataStore = metadataStore;
    }

    private bool CanBrowse() => !IsCropping;

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private void BrowseForSource()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a full-frame .ser file to crop",
            Filter = "SER video files (*.ser)|*.ser|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(CaptureLocations.DefaultCapturesRootFolder)
                ? CaptureLocations.DefaultCapturesRootFolder
                : null,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SourceFilePath = dialog.FileName;
        LoadSourceHeader();
    }

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private void BrowseForOutput()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Choose where to save the cropped .ser file",
            Filter = "SER video files (*.ser)|*.ser|All files (*.*)|*.*",
            FileName = OutputFilePath is not null ? Path.GetFileName(OutputFilePath) : null,
            InitialDirectory = OutputFilePath is not null ? Path.GetDirectoryName(OutputFilePath) : null,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        OutputFilePath = dialog.FileName;
    }

    private void LoadSourceHeader()
    {
        _sourceHeader = null;
        SourceInfoText = null;
        CroppedHeightPreviewText = null;

        if (SourceFilePath is null)
        {
            return;
        }

        try
        {
            using var reader = _serReaderFactory();
            reader.Open(SourceFilePath);
            _sourceHeader = reader.Header;
            SourceInfoText = $"{_sourceHeader.Width} x {_sourceHeader.Height}, {_sourceHeader.PixelDepth}-bit, {_sourceHeader.FrameCount} frame(s)";
            OutputFilePath = SuggestOutputPath(SourceFilePath);
            UpdateCroppedHeightPreview();
            StatusText = "Ready. Adjust the height percentage and output file, then click Crop.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusText = $"Failed to read '{SourceFilePath}': {ex.Message}";
        }

        CropCommand.NotifyCanExecuteChanged();
    }

    private static string SuggestOutputPath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var fileName = $"{name}_cropped.ser";
        return directory is null ? fileName : Path.Combine(directory, fileName);
    }

    partial void OnHeightPercentChanged(double value)
    {
        UpdateCroppedHeightPreview();
        CropCommand.NotifyCanExecuteChanged();
    }

    private void UpdateCroppedHeightPreview()
    {
        if (_sourceHeader is not { } header || HeightPercent <= 0 || HeightPercent > 100)
        {
            CroppedHeightPreviewText = null;
            return;
        }

        var croppedHeight = SerCropper.ComputeCroppedHeight(header.Height, HeightPercent / 100.0);
        var rowOffset = (header.Height - croppedHeight) / 2;
        CroppedHeightPreviewText = $"Cropped height: {croppedHeight}px (rows {rowOffset}-{rowOffset + croppedHeight - 1} of {header.Height}, "
            + $"same {header.Width}px width).";
    }

    private bool CanCrop() =>
        !IsCropping && _sourceHeader is not null && !string.IsNullOrWhiteSpace(OutputFilePath)
        && HeightPercent > 0 && HeightPercent <= 100;

    [RelayCommand(CanExecute = nameof(CanCrop))]
    private async Task CropAsync()
    {
        if (SourceFilePath is null || OutputFilePath is null)
        {
            return;
        }

        _cropCts = new CancellationTokenSource();
        try
        {
            IsCropping = true;
            var progress = new Progress<string>(s => StatusText = s);
            var result = await _serCropper.CropAsync(SourceFilePath, OutputFilePath, HeightPercent / 100.0, progress, _cropCts.Token);

            CopyEquipmentSidecarIfPresent(SourceFilePath, OutputFilePath);

            StatusText = $"Wrote {result.FrameCount} frame(s), {result.Width}x{result.CroppedHeight}, to '{OutputFilePath}'.";
        }
        catch (OperationCanceledException)
        {
            TryDeletePartialOutput();
            StatusText = "Cropping cancelled.";
        }
        catch (Exception ex)
        {
            TryDeletePartialOutput();
            StatusText = $"Cropping failed: {ex.Message}";
        }
        finally
        {
            IsCropping = false;
            _cropCts?.Dispose();
            _cropCts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsCropping))]
    private void CancelCrop() => _cropCts?.Cancel();

    /// <summary>Best-effort: a file cancelled/failed part-way through shouldn't be left looking like a
    /// complete crop. Swallows IO failures (e.g. the file is still briefly locked right after
    /// SerWriter.Close disposes its stream) rather than throwing out of a cancellation/failure path.</summary>
    private void TryDeletePartialOutput()
    {
        try
        {
            if (OutputFilePath is not null && File.Exists(OutputFilePath))
            {
                File.Delete(OutputFilePath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Copies the source recording's .equipment.json sidecar (if any) alongside the cropped
    /// output - the equipment/camera-settings/mount-pointing snapshot it carries (see
    /// <see cref="CaptureMetadata"/>) is unaffected by cropping, so the cropped file should still show
    /// the same equipment info on Process's own file picker as the original did.</summary>
    private void CopyEquipmentSidecarIfPresent(string sourcePath, string outputPath)
    {
        if (_metadataStore.TryRead(sourcePath) is { } metadata)
        {
            _metadataStore.Write(outputPath, metadata);
        }
    }

    partial void OnIsCroppingChanged(bool value)
    {
        BrowseForSourceCommand.NotifyCanExecuteChanged();
        BrowseForOutputCommand.NotifyCanExecuteChanged();
        CropCommand.NotifyCanExecuteChanged();
        CancelCropCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutputFilePathChanged(string? value) => CropCommand.NotifyCanExecuteChanged();
}
