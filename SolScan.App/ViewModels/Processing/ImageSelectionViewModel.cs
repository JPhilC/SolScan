using CommunityToolkit.Mvvm.ComponentModel;
using SolScan.Core.Processing;

namespace SolScan.App.ViewModels.Processing;

/// <summary>
/// Process view's "Image Selection" section (moved here from the old Options tab of the same name) -
/// one flat checklist over every <see cref="GeneratedImageKind"/> SolScan v1 produces, with no
/// separate "advanced" tier - see that enum's own doc comment for why SolScan doesn't carry forward
/// JSolex's own Basic/Advanced Images split. Debug Options/Custom Images/scripts/presets are still out
/// of scope - see SolScan CLAUDE.md. No store access of its own - see
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

    [ObservableProperty]
    private bool isColorizedSelected;

    [ObservableProperty]
    private bool isVirtualEclipseSelected;

    public ImageSelectionViewModel(ProcessParams processParams)
    {
        var images = processParams.RequestedImages;
        isRawSelected = images.IsEnabled(GeneratedImageKind.Raw);
        isReconstructionSelected = images.IsEnabled(GeneratedImageKind.Reconstruction);
        isContinuumSelected = images.IsEnabled(GeneratedImageKind.Continuum);
        isGeometryCorrectedSelected = images.IsEnabled(GeneratedImageKind.GeometryCorrected);
        isGeometryCorrectedProcessedSelected = images.IsEnabled(GeneratedImageKind.GeometryCorrectedProcessed);
        isColorizedSelected = images.IsEnabled(GeneratedImageKind.Colorized);
        isVirtualEclipseSelected = images.IsEnabled(GeneratedImageKind.VirtualEclipse);
    }

    public RequestedImages ToRequestedImages()
    {
        var images = new HashSet<GeneratedImageKind>();
        if (IsRawSelected) images.Add(GeneratedImageKind.Raw);
        if (IsReconstructionSelected) images.Add(GeneratedImageKind.Reconstruction);
        if (IsContinuumSelected) images.Add(GeneratedImageKind.Continuum);
        if (IsGeometryCorrectedSelected) images.Add(GeneratedImageKind.GeometryCorrected);
        if (IsGeometryCorrectedProcessedSelected) images.Add(GeneratedImageKind.GeometryCorrectedProcessed);
        if (IsColorizedSelected) images.Add(GeneratedImageKind.Colorized);
        if (IsVirtualEclipseSelected) images.Add(GeneratedImageKind.VirtualEclipse);
        return new RequestedImages(images);
    }
}
