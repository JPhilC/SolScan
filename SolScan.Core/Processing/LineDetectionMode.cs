// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/params/LineDetectionMode.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>How the spectral line being observed is determined - direct port of astro4j's
/// <c>LineDetectionMode</c> enum. Real, not just a persisted-but-unused choice: see
/// <see cref="SolScan.Processing.Shg.ShgProcessor"/>'s own doc comment for where
/// <see cref="SolScan.Processing.Spectrum.SpectralLineIdentifier"/> is actually consulted.</summary>
public enum LineDetectionMode
{
    /// <summary>Searched for among all the deep lines of the solar spectrum, so a line with no
    /// catalog entry can still be identified. Falls back to <see cref="Auto"/> when no line can be
    /// identified with confidence. Not yet distinguished from <see cref="Auto"/> in SolScan - that
    /// whole-spectrum search would need a much larger reference atlas than the 12 narrow, named-line
    /// windows <see cref="SolScan.Processing.Spectrum.SpectralLineIdentifier"/> currently bundles, so
    /// this behaves identically to <see cref="Auto"/> for now, a documented simplification rather than
    /// an oversight.</summary>
    FreeSearch,

    /// <summary>Searched for among the user's own configured/predefined lines only - SolScan's 12
    /// named <see cref="SpectralRay"/> values, correlated against a bundled reference atlas. A
    /// confident result overrides <see cref="SpectrumParams.Ray"/> for that run; an unconfident one
    /// (or a file with no usable equipment info to compute dispersion from) falls back to it.</summary>
    Auto,

    /// <summary>The user picked the line themselves - no detection performed.</summary>
    Manual,
}
