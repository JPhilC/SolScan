using CommunityToolkit.Mvvm.ComponentModel;
using SolScan.Core.Processing;

namespace SolScan.App.ViewModels.Processing;

/// <summary>
/// Options > Image Enhancement tab - just the contrast-enhancement method choice for now (see
/// <see cref="ContrastEnhancementMode"/>'s own doc comment for what's not ported yet: CLAHE/
/// AutoStretch tuning, deconvolution, sharpening, flat correction, banding/jagging/oscillation
/// correction). No store access of its own - see <see cref="ProcessParametersViewModel"/>'s doc
/// comment for why.
/// </summary>
public partial class ImageEnhancementViewModel : ObservableObject
{
    public IReadOnlyList<ContrastEnhancementMode> AvailableContrastEnhancementModes { get; } =
        Enum.GetValues<ContrastEnhancementMode>();

    [ObservableProperty]
    private ContrastEnhancementMode selectedContrastEnhancement;

    public ImageEnhancementViewModel(ProcessParams processParams)
    {
        selectedContrastEnhancement = processParams.ContrastEnhancement;
    }

    public ContrastEnhancementMode ToContrastEnhancement() => SelectedContrastEnhancement;
}
