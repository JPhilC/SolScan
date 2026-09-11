using CommunityToolkit.Mvvm.ComponentModel;
using SolScan.Core.Processing;

namespace SolScan.App.ViewModels.Processing;

/// <summary>
/// Options > Process Parameters tab: which spectral line is being studied and how its pixel shifts/
/// geometry are set up - the slice of astro4j's "Process parameters" dialog page SolScan needs for
/// its Basic Images to mean something (see <see cref="SpectrumParams"/>/<see cref="GeometryParams"/>'s
/// own doc comments for what's deliberately not ported yet). No store access of its own - seeded from
/// a <see cref="ProcessParams"/> passed in by <see cref="OptionsViewModel"/>, and read back out via
/// <see cref="ToSpectrumParams"/>/<see cref="ToGeometryParams"/> when <c>OptionsViewModel.Save</c>
/// reassembles the single persisted record all three new tabs share.
/// </summary>
public partial class ProcessParametersViewModel : ObservableObject
{
    public IReadOnlyList<SpectralRay> AvailableRays => SpectralRay.Predefined;
    public IReadOnlyList<LineDetectionMode> AvailableDetectionModes { get; } = Enum.GetValues<LineDetectionMode>();
    public IReadOnlyList<RotationKind> AvailableRotations { get; } = Enum.GetValues<RotationKind>();
    public IReadOnlyList<AutocropMode> AvailableAutocropModes { get; } = Enum.GetValues<AutocropMode>();

    [ObservableProperty]
    private SpectralRay selectedRay;

    [ObservableProperty]
    private LineDetectionMode detectionMode;

    [ObservableProperty]
    private double pixelShift;

    [ObservableProperty]
    private double dopplerShift;

    [ObservableProperty]
    private double continuumShift;

    [ObservableProperty]
    private bool switchRedBlueChannels;

    [ObservableProperty]
    private RotationKind rotation;

    // Named SelectedAutocropMode, not AutocropMode - CommunityToolkit's source generator would
    // otherwise produce a property with the exact same name as the SolScan.Core.Processing.AutocropMode
    // enum it holds, the same name/type collision ContrastEnhancementMode was renamed to avoid.
    [ObservableProperty]
    private AutocropMode selectedAutocropMode;

    [ObservableProperty]
    private int? fixedWidth;

    [ObservableProperty]
    private bool horizontalMirror;

    [ObservableProperty]
    private bool verticalMirror;

    public ProcessParametersViewModel(ProcessParams processParams)
    {
        var spectrum = processParams.SpectrumParams;
        var geometry = processParams.GeometryParams;

        selectedRay = spectrum.Ray;
        detectionMode = spectrum.DetectionMode;
        pixelShift = spectrum.PixelShift;
        dopplerShift = spectrum.DopplerShift;
        continuumShift = spectrum.ContinuumShift;
        switchRedBlueChannels = spectrum.SwitchRedBlueChannels;

        rotation = geometry.Rotation;
        selectedAutocropMode = geometry.AutocropMode;
        fixedWidth = geometry.FixedWidth;
        horizontalMirror = geometry.HorizontalMirror;
        verticalMirror = geometry.VerticalMirror;
    }

    public SpectrumParams ToSpectrumParams() => new(
        SelectedRay, DetectionMode, PixelShift, DopplerShift, ContinuumShift, SwitchRedBlueChannels);

    public GeometryParams ToGeometryParams() => new(
        Rotation, SelectedAutocropMode, FixedWidth, HorizontalMirror, VerticalMirror);
}
