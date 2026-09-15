namespace SolScan.Core.Camera;

/// <summary>
/// One entry in <see cref="ExposureScale.Ranges"/> - the ASICap-style Exposure range dropdown's
/// options. <see cref="Unit"/>/<see cref="UnitMicroseconds"/> convert the numeric up/down box's own
/// displayed value (always expressed in this range's unit) to/from the raw microseconds
/// <see cref="ICameraDevice.ExposureMicroseconds"/> actually stores; <see cref="StepMicroseconds"/>
/// is how far one spinner click moves it. <see cref="DecimalPlaces"/> is how much fractional
/// precision the box/slider allow in this range's own unit - 0 (whole µs/ms only) for every range
/// except the seconds one, matching ASICap: only its long-exposure range allows fractional values.
/// </summary>
public readonly record struct ExposureRangeOption(
    string Label,
    double MinMicroseconds,
    double MaxMicroseconds,
    string Unit,
    double UnitMicroseconds,
    double StepMicroseconds,
    int DecimalPlaces)
{
    public override string ToString() => Label;
}
