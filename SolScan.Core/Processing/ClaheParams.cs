// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/params/ClaheParams.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>
/// Tuning parameters for <see cref="ContrastEnhancementMode.Clahe"/> - tile size, histogram bin count,
/// and clip limit. See <c>SolScan.Processing.Stretching.ClaheStrategy</c> for what each does.
/// </summary>
public sealed record ClaheParams(int TileSize, int Bins, double Clipping)
{
    /// <summary>Matches <c>SolScan.Processing.Stretching.ClaheStrategy.DefaultTileSize/DefaultBins/
    /// DefaultClip</c> exactly - duplicated as literals rather than referenced directly, since
    /// SolScan.Core has no dependency on SolScan.Processing (the same one-way layering every other
    /// Core/Processing split already respects - see CLAUDE.md's "Project layering" section).</summary>
    public static ClaheParams Default { get; } = new(8, 64, 1.0);
}
