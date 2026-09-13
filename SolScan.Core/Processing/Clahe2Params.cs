// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/expr/impl/Clahe.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>
/// Tuning parameters for <see cref="ContrastEnhancementMode.Clahe2"/> (multi-scale CLAHE) - just the
/// clip limit. Unlike <see cref="ClaheParams"/>, astro4j's own CLAHE2 UI doesn't expose a tile size or
/// bin count for this mode either - those are fixed internal constants
/// (<c>SolScan.Processing.Stretching.MultiScaleClaheStrategy.Bins</c>/<c>MaxLevels</c>, derived instead
/// from the disk's own diameter), so SolScan matches that.
/// </summary>
public sealed record Clahe2Params(double Clipping)
{
    /// <summary>Matches <c>SolScan.Processing.Stretching.MultiScaleClaheStrategy.DefaultClip</c> - see
    /// <see cref="ClaheParams.Default"/>'s own doc comment for why this is a duplicated literal rather
    /// than a direct reference.</summary>
    public static Clahe2Params Default { get; } = new(1.5);
}
