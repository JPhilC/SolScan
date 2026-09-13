using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolScan.Core.Processing;

namespace SolScan.App.ViewModels.Processing;

/// <summary>
/// Process view's "Image Enhancement" section - the contrast-enhancement method choice plus each
/// method's own tuning parameters, surfaced the way JSolex's own desktop UI does
/// (`ImageEnhancementPanel.java`): fixed dropdowns for CLAHE's tile size/bins (astro4j's own discrete
/// option lists, with the same bins-must-not-exceed-tileSize² cross-validation), free-text fields for
/// everything else (clip limits, gamma, background threshold, prominence stretch - JSolex doesn't use
/// sliders or declare numeric ranges for these beyond the guards <c>AutoStretchStrategy</c> itself
/// enforces). No store access of its own - see <see cref="ProcessParametersViewModel"/>'s doc comment
/// for why (moved here nearly unchanged from that same reasoning, just now owned by
/// <c>ProcessViewModel</c> instead of the old Options tab).
/// </summary>
public partial class ImageEnhancementViewModel : ObservableObject
{
    public IReadOnlyList<ContrastEnhancementMode> AvailableContrastEnhancementModes { get; } =
        Enum.GetValues<ContrastEnhancementMode>();

    /// <summary>JSolex's own fixed discrete tile-size choices (`ImageEnhancementPanel.java`) - CLAHE
    /// tile sizes are chosen from a list, not typed freely.</summary>
    public static IReadOnlyList<int> AvailableClaheTileSizes { get; } = [8, 16, 32, 64, 128, 256, 512, 1024];

    /// <summary>The full set of bin counts JSolex offers - <see cref="AvailableClaheBins"/> filters this
    /// down to whichever of these don't exceed the current <see cref="ClaheTileSize"/> squared.</summary>
    private static IReadOnlyList<int> AllClaheBins { get; } = [32, 64, 128, 256, 512, 1024];

    [ObservableProperty]
    private ContrastEnhancementMode selectedContrastEnhancement;

    [ObservableProperty]
    private int claheTileSize;

    [ObservableProperty]
    private int claheBins;

    [ObservableProperty]
    private double claheClipping;

    [ObservableProperty]
    private double clahe2Clipping;

    [ObservableProperty]
    private double autoStretchGamma;

    [ObservableProperty]
    private double autoStretchBackgroundThreshold;

    [ObservableProperty]
    private double autoStretchProtusStretch;

    /// <summary>Bound as the Bins <c>ComboBox</c>'s <c>ItemsSource</c> - a live, mutated-in-place
    /// collection (not reassigned) so the binding keeps tracking it across <see cref="ClaheTileSize"/>
    /// changes.</summary>
    public ObservableCollection<int> AvailableClaheBins { get; } = [];

    public ImageEnhancementViewModel(ProcessParams processParams)
    {
        selectedContrastEnhancement = processParams.ContrastEnhancement;
        claheTileSize = processParams.ClaheParams.TileSize;
        claheBins = processParams.ClaheParams.Bins;
        claheClipping = processParams.ClaheParams.Clipping;
        clahe2Clipping = processParams.Clahe2Params.Clipping;
        autoStretchGamma = processParams.AutoStretchParams.Gamma;
        autoStretchBackgroundThreshold = processParams.AutoStretchParams.BackgroundThreshold;
        autoStretchProtusStretch = processParams.AutoStretchParams.ProtusStretch;
        UpdateAvailableClaheBins();
    }

    /// <summary>Same cross-validation JSolex's own tile-size/bins listener enforces
    /// (`ImageEnhancementPanel.java`): a tile of <see cref="ClaheTileSize"/>² pixels can't usefully
    /// support more histogram bins than it has pixels. Clamps <see cref="ClaheBins"/> down to the new
    /// list's largest remaining option if the previous choice is no longer valid.</summary>
    partial void OnClaheTileSizeChanged(int value) => UpdateAvailableClaheBins();

    private void UpdateAvailableClaheBins()
    {
        var maxBins = ClaheTileSize * ClaheTileSize;
        AvailableClaheBins.Clear();
        foreach (var bins in AllClaheBins)
        {
            if (bins <= maxBins)
            {
                AvailableClaheBins.Add(bins);
            }
        }

        if (AvailableClaheBins.Count > 0 && !AvailableClaheBins.Contains(ClaheBins))
        {
            ClaheBins = AvailableClaheBins[^1];
        }
    }

    /// <summary>Snaps every tuning field back to its algorithm's own built-in default
    /// (<see cref="ClaheParams.Default"/>/<see cref="Clahe2Params.Default"/>/<see cref="AutoStretchParams.Default"/>)
    /// - does not touch <see cref="SelectedContrastEnhancement"/> itself, only the tuning values
    /// underneath whichever mode is (or might later be) selected.</summary>
    [RelayCommand]
    private void ResetToDefaults()
    {
        var claheDefault = ClaheParams.Default;
        ClaheTileSize = claheDefault.TileSize;
        UpdateAvailableClaheBins();
        ClaheBins = claheDefault.Bins;
        ClaheClipping = claheDefault.Clipping;

        Clahe2Clipping = Clahe2Params.Default.Clipping;

        var autoStretchDefault = AutoStretchParams.Default;
        AutoStretchGamma = autoStretchDefault.Gamma;
        AutoStretchBackgroundThreshold = autoStretchDefault.BackgroundThreshold;
        AutoStretchProtusStretch = autoStretchDefault.ProtusStretch;
    }

    public ContrastEnhancementMode ToContrastEnhancement() => SelectedContrastEnhancement;

    public ClaheParams ToClaheParams() => new(ClaheTileSize, ClaheBins, ClaheClipping);

    public Clahe2Params ToClahe2Params() => new(Clahe2Clipping);

    public AutoStretchParams ToAutoStretchParams() => new(AutoStretchGamma, AutoStretchBackgroundThreshold, AutoStretchProtusStretch);
}
