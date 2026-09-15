namespace SolScan.Core.Camera;

/// <summary>
/// The ASICap-style set of Exposure sub-ranges covering <see cref="MinMicroseconds"/>-<see
/// cref="MaxMicroseconds"/> (0.032ms-5s, the ASI678MM's own non-"long exposure mode" range - see
/// <see cref="ICameraDevice.ExposureMicroseconds"/>'s doc comment). SolScan.App's Exposure control
/// mirrors ASICap's own real UI: a dropdown picks which of these sub-ranges the numeric up/down box
/// and slider currently work within (each in its own natural unit), rather than one control trying
/// to cover five decades of range at once. The ranges mirror ASICap's own fixed list (32µs~10ms,
/// 1~100ms, 100~1000ms, then a long-exposure range) except the last one is capped at 5s rather than
/// ASICap's own 2000s, since LX mode itself isn't supported here. Kept independent of WPF so it's
/// unit-testable on its own (see SolScan.Tests).
/// </summary>
public static class ExposureScale
{
    public const double MinMicroseconds = 32; // 0.032ms
    public const double MaxMicroseconds = 5_000_000; // 5s

    // DecimalPlaces is 0 (whole units only) for every range except the last - matching ASICap,
    // where only its long-exposure range accepts fractional values.
    public static readonly IReadOnlyList<ExposureRangeOption> Ranges =
    [
        new("32µs ~ 10ms", MinMicroseconds, 10_000, "µs", 1, 1, DecimalPlaces: 0),
        new("1ms ~ 100ms", 1_000, 100_000, "ms", 1_000, 1_000, DecimalPlaces: 0),
        new("100ms ~ 1000ms", 100_000, 1_000_000, "ms", 1_000, 1_000, DecimalPlaces: 0),
        new("1s ~ 5s", 1_000_000, MaxMicroseconds, "s", 1_000_000, 100_000, DecimalPlaces: 3),
    ];

    /// <summary>The range whose [Min,Max] contains <paramref name="microseconds"/> - the first
    /// match in <see cref="Ranges"/> wins wherever two ranges share a boundary (e.g. exactly 10ms
    /// matches "32µs ~ 10ms", not "1ms ~ 100ms"). Falls back to the first/last range for a value
    /// outside <see cref="MinMicroseconds"/>-<see cref="MaxMicroseconds"/> entirely, since a caller
    /// should already have clamped to that domain.</summary>
    public static ExposureRangeOption FindRange(double microseconds)
    {
        foreach (var range in Ranges)
        {
            if (microseconds >= range.MinMicroseconds && microseconds <= range.MaxMicroseconds)
            {
                return range;
            }
        }

        return microseconds < MinMicroseconds ? Ranges[0] : Ranges[^1];
    }
}
