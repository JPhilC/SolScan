namespace SolScan.Core.Equipment;

/// <summary>
/// A spectroheliograph's own optical/mechanical parameters - independent of whatever telescope,
/// camera, or mount it happens to be bolted to for a given session. See <see cref="EquipmentProfile"/>
/// for the telescope/camera side, and <see cref="EquipmentSetup"/> for a saved SHG+equipment
/// combination.
///
/// Close translation of astro4j's SpectroHeliograph.java (jsolex-core, Apache-2.0) - see NOTICE.
/// Field names are adapted to be self-describing (e.g. FocalLength -> CameraFocalLengthMm) rather
/// than matching the Java record's exact names, and a persisted <see cref="Id"/> is added since
/// SolScan's library needs stable identity for <see cref="EquipmentSetup"/> to reference.
/// </summary>
public sealed record SpectrographProfile(
    Guid Id,
    string Label,
    double TotalAngleDegrees,
    double CameraFocalLengthMm,
    double CollimatorFocalLengthMm,
    int GratingDensityLinesPerMm,
    int DiffractionOrder,
    double SlitWidthMicrons,
    double SlitHeightMm,
    bool SpectrumVFlip)
{
    public double TotalAngleRadians => TotalAngleDegrees * Math.PI / 180.0;

    /// <summary>
    /// The SHG's own re-imaging ratio (camera focal length / collimator focal length) - the same
    /// factor astro4j's ExposureCalculator.java uses to scale the disk image formed at the slit
    /// onto the sensor (see SolScan CLAUDE.md's ExposureCalculator note, Phase 4).
    /// </summary>
    public double ReimagingRatio => CameraFocalLengthMm / CollimatorFocalLengthMm;

    public static SpectrographProfile CreateSolEx() => new(
        Guid.NewGuid(), "Sol'Ex", 34, 125, 80, 2400, 1, 10, 4.5, false);

    public static SpectrographProfile CreateSolEx7() => new(
        Guid.NewGuid(), "Sol'Ex (7µm/6mm slit)", 34, 125, 80, 2400, 1, 7, 6, false);

    public static SpectrographProfile CreateSolEx10() => new(
        Guid.NewGuid(), "Sol'Ex (10µm/6mm slit)", 34, 125, 80, 2400, 1, 10, 6, false);

    public static SpectrographProfile CreateSunscan() => new(
        Guid.NewGuid(), "Sunscan", 34, 100, 75, 2400, 1, 10, 6, true);

    /// <summary>Seed values for a first-run library, matching astro4j's SpectroHeliographsIO.predefined().</summary>
    public static IReadOnlyList<SpectrographProfile> Predefined() =>
        [CreateSolEx(), CreateSolEx10(), CreateSolEx7(), CreateSunscan()];
}
