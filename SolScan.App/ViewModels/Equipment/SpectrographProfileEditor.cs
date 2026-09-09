using CommunityToolkit.Mvvm.ComponentModel;
using SolScan.Core.Equipment;

namespace SolScan.App.ViewModels.Equipment;

/// <summary>
/// Mutable, bindable wrapper around an (immutable) SpectrographProfile record, so WPF's two-way
/// TextBox/CheckBox bindings have something to write into - mirrors astro4j's
/// SpectroHeliographEditor.java, which similarly rebuilds a new record from form-field values on
/// every edit rather than binding directly to the record.
/// </summary>
public partial class SpectrographProfileEditor : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty]
    private string label;

    [ObservableProperty]
    private double totalAngleDegrees;

    [ObservableProperty]
    private double cameraFocalLengthMm;

    [ObservableProperty]
    private double collimatorFocalLengthMm;

    [ObservableProperty]
    private int gratingDensityLinesPerMm;

    [ObservableProperty]
    private int diffractionOrder;

    [ObservableProperty]
    private double slitWidthMicrons;

    [ObservableProperty]
    private double slitHeightMm;

    [ObservableProperty]
    private bool spectrumVFlip;

    public SpectrographProfileEditor(SpectrographProfile profile)
    {
        Id = profile.Id;
        label = profile.Label;
        totalAngleDegrees = profile.TotalAngleDegrees;
        cameraFocalLengthMm = profile.CameraFocalLengthMm;
        collimatorFocalLengthMm = profile.CollimatorFocalLengthMm;
        gratingDensityLinesPerMm = profile.GratingDensityLinesPerMm;
        diffractionOrder = profile.DiffractionOrder;
        slitWidthMicrons = profile.SlitWidthMicrons;
        slitHeightMm = profile.SlitHeightMm;
        spectrumVFlip = profile.SpectrumVFlip;
    }

    public SpectrographProfile ToModel() => new(
        Id,
        Label,
        TotalAngleDegrees,
        CameraFocalLengthMm,
        CollimatorFocalLengthMm,
        GratingDensityLinesPerMm,
        DiffractionOrder,
        SlitWidthMicrons,
        SlitHeightMm,
        SpectrumVFlip);
}
