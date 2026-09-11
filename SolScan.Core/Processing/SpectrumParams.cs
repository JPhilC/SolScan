// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/params/SpectrumParams.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>
/// Which spectral line is being studied and how the reconstruction's pixel shifts are chosen -
/// ported from astro4j's <c>SpectrumParams</c>, trimmed to the fields SolScan's Basic Images actually
/// need (dropped nothing beyond that - this is the full JSolex record).
/// </summary>
/// <param name="Ray">The spectral line being studied - currently always picked by hand in Options >
/// Process Parameters. Once Capture's live line-identification overlay exists (CLAUDE.md Phase 4),
/// this should instead be derivable from it automatically - whichever labeled line sits nearest the
/// ROI's vertical centre - rather than requiring a separate manual pick here; that's a future wiring
/// change, not yet built.</param>
/// <param name="DetectionMode">How <see cref="Ray"/>'s exact position in the frame is determined.</param>
/// <param name="PixelShift">Pixel shift (relative to the detected line centre) used for the main
/// reconstruction - 0 means exactly on the line centre.</param>
/// <param name="DopplerShift">Pixel shift used for Doppler-related images - not yet consumed by
/// anything in SolScan (Doppler is an Advanced Images kind, not yet ported), carried here so the
/// field exists once it is.</param>
/// <param name="ContinuumShift">Pixel shift used for <see cref="GeneratedImageKind.Continuum"/>.</param>
/// <param name="SwitchRedBlueChannels">Swaps the red/blue channels on colorized output - not yet
/// consumed by anything in SolScan (colorized output is an Advanced Images kind, not yet ported).</param>
public sealed record SpectrumParams(
    SpectralRay Ray,
    LineDetectionMode DetectionMode,
    double PixelShift,
    double DopplerShift,
    double ContinuumShift,
    bool SwitchRedBlueChannels)
{
    /// <summary>The three shifts default to 0 - a deliberately neutral placeholder, not a guessed
    /// "realistic" value, since there's no real pipeline yet to validate a better default against.</summary>
    public static SpectrumParams Default { get; } =
        new(SpectralRay.HAlpha, LineDetectionMode.Auto, 0, 0, 0, false);
}
