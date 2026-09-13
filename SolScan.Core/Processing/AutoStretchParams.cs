// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/params/AutoStretchParams.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>
/// Tuning parameters for <see cref="ContrastEnhancementMode.AutoStretch"/> - see
/// <c>SolScan.Processing.Stretching.AutoStretchStrategy</c> for what each does and the valid ranges it
/// enforces (<paramref name="Gamma"/> &gt; 1, <paramref name="BackgroundThreshold"/> in (0, 1],
/// <paramref name="ProtusStretch"/> &gt;= 0).
/// </summary>
public sealed record AutoStretchParams(double Gamma, double BackgroundThreshold, double ProtusStretch)
{
    /// <summary>Matches <c>AutoStretchStrategy.DefaultGamma/DefaultBackgroundThreshold/
    /// DefaultProtusStretch</c> - see <see cref="ClaheParams.Default"/>'s own doc comment for why this
    /// is a duplicated literal rather than a direct reference.</summary>
    public static AutoStretchParams Default { get; } = new(1.5, 0.5, 0);
}
