using CommunityToolkit.Mvvm.ComponentModel;
using SolScan.Core.Processing;

namespace SolScan.App.ViewModels.Processing;

/// <summary>
/// Options > Image Selection tab - the "Basic Images" checklist from astro4j's "Image Selection and
/// Scripts" page (Advanced Images/Debug Options/Custom Images/scripts/presets are all out of scope
/// for now - see SolScan CLAUDE.md). No store access of its own - see
/// <see cref="ProcessParametersViewModel"/>'s doc comment for why.
/// </summary>
public partial class ImageSelectionViewModel : ObservableObject
{
    [ObservableProperty]
    private bool isRawSelected;

    [ObservableProperty]
    private bool isReconstructionSelected;

    [ObservableProperty]
    private bool isContinuumSelected;

    [ObservableProperty]
    private bool isGeometryCorrectedSelected;

    [ObservableProperty]
    private bool isGeometryCorrectedProcessedSelected;

    public ImageSelectionViewModel(ProcessParams processParams)
    {
        var images = processParams.RequestedImages;
        isRawSelected = images.IsEnabled(GeneratedImageKind.Raw);
        isReconstructionSelected = images.IsEnabled(GeneratedImageKind.Reconstruction);
        isContinuumSelected = images.IsEnabled(GeneratedImageKind.Continuum);
        isGeometryCorrectedSelected = images.IsEnabled(GeneratedImageKind.GeometryCorrected);
        isGeometryCorrectedProcessedSelected = images.IsEnabled(GeneratedImageKind.GeometryCorrectedProcessed);
    }

    public RequestedImages ToRequestedImages()
    {
        var images = new HashSet<GeneratedImageKind>();
        if (IsRawSelected) images.Add(GeneratedImageKind.Raw);
        if (IsReconstructionSelected) images.Add(GeneratedImageKind.Reconstruction);
        if (IsContinuumSelected) images.Add(GeneratedImageKind.Continuum);
        if (IsGeometryCorrectedSelected) images.Add(GeneratedImageKind.GeometryCorrected);
        if (IsGeometryCorrectedProcessedSelected) images.Add(GeneratedImageKind.GeometryCorrectedProcessed);
        return new RequestedImages(images);
    }
}
