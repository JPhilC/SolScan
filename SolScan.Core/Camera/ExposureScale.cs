namespace SolScan.Core.Camera;

/// <summary>
/// Maps <see cref="ICameraDevice.ExposureMicroseconds"/> to/from a 0-1 slider position on a
/// logarithmic scale, matching SharpCap's exposure slider for the ASI678MM (0.032ms-5s, its
/// non-"long exposure mode" range) - a linear slider is useless across five decades of range, since
/// almost the entire useful sub-second SHG-scanning region would be squashed into the first sliver
/// of travel. Kept independent of WPF so it's unit-testable on its own (see SolScan.Tests).
/// </summary>
public static class ExposureScale
{
    public const double MinMicroseconds = 32; // 0.032ms
    public const double MaxMicroseconds = 5_000_000; // 5s

    private static readonly double LogMin = Math.Log(MinMicroseconds);
    private static readonly double LogMax = Math.Log(MaxMicroseconds);

    /// <summary>Microseconds -> 0-1 slider position.</summary>
    public static double ToSliderPosition(double microseconds)
    {
        var clamped = Math.Clamp(microseconds, MinMicroseconds, MaxMicroseconds);
        return (Math.Log(clamped) - LogMin) / (LogMax - LogMin);
    }

    /// <summary>0-1 slider position -> microseconds.</summary>
    public static double FromSliderPosition(double position)
    {
        var clamped = Math.Clamp(position, 0, 1);
        return Math.Exp(LogMin + (clamped * (LogMax - LogMin)));
    }

    /// <summary>SharpCap-style display text - µs/ms/s, whichever reads most naturally at this
    /// magnitude.</summary>
    public static string Format(double microseconds) => microseconds switch
    {
        < 1_000 => $"{microseconds:0} µs",
        < 1_000_000 => $"{microseconds / 1_000:0.0} ms",
        _ => $"{microseconds / 1_000_000:0.000} s",
    };
}
