// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/params/LineDetectionMode.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>How the spectral line being observed is determined - direct port of astro4j's
/// <c>LineDetectionMode</c> enum.</summary>
public enum LineDetectionMode
{
    /// <summary>Searched for among all the deep lines of the solar spectrum, so a line with no
    /// catalog entry can still be identified. Falls back to <see cref="Auto"/> when no line can be
    /// identified with confidence.</summary>
    FreeSearch,

    /// <summary>Searched for among the user's own configured/predefined lines only.</summary>
    Auto,

    /// <summary>The user picked the line themselves - no detection performed.</summary>
    Manual,
}
