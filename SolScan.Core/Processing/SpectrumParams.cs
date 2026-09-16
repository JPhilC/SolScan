// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/params/SpectrumParams.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>
/// Which spectral line is being studied and how the reconstruction's pixel shifts are chosen -
/// ported from astro4j's <c>SpectrumParams</c>, trimmed to the fields SolScan's own output images
/// actually need (dropped nothing beyond that - this is the full JSolex record).
/// </summary>
/// <param name="Ray">The spectral line being studied - picked by hand on the Process view's own Process
/// Parameters panel, and always what's used when <paramref name="DetectionMode"/> is
/// <see cref="LineDetectionMode.Manual"/>. With <see cref="LineDetectionMode.Auto"/>/
/// <see cref="LineDetectionMode.FreeSearch"/> it's only the fallback - see <see cref="DetectionMode"/>'s
/// own doc comment - used whenever automatic identification (<see cref="SolScan.Processing.Spectrum.SpectralLineIdentifier"/>)
/// can't confidently recognise the captured line on its own. Capture's live line-identification overlay
/// (CLAUDE.md Phase 4) *does* now feed this indirectly: its own last confident identification is
/// stamped onto <see cref="SolScan.Core.Capture.CaptureMetadata.StudiedRay"/> at recording time, and
/// <c>ProcessViewModel.LoadSelectedFile</c> prefills this field from that sidecar value directly (and
/// skips offline identification entirely for that file) whenever it's present - a much more reliable
/// source than either a manual pick or the offline identifier, since the live overlay sees the wide,
/// uncropped preview rather than an already-cropped recording with little spectral context left to
/// identify from.</param>
/// <param name="DetectionMode">How <see cref="Ray"/>'s identity (not its position in the frame, which is
/// always just whichever absorption line is most prominent) is determined - real, not just persisted;
/// see <see cref="SolScan.Processing.Shg.ShgProcessor"/>'s own doc comment for where this is consulted.</param>
/// <param name="PixelShift">Pixel shift (relative to the detected line centre) used for the main
/// reconstruction - 0 means exactly on the line centre.</param>
/// <param name="DopplerShift">Pixel shift used for Doppler-related images - not yet consumed by
/// anything in SolScan (Doppler is an Advanced Images kind, not yet ported), carried here so the
/// field exists once it is.</param>
/// <param name="ContinuumShift">Pixel shift used for <see cref="GeneratedImageKind.Continuum"/>.</param>
/// <param name="SwitchRedBlueChannels">Swaps which of the two opposite-shift wing images is treated
/// as red vs. blue for Doppler images (astro4j's <c>DopplerSupport</c>) - not colorized output, despite
/// this field's name; not yet consumed by anything in SolScan (Doppler is an Advanced Images kind,
/// not yet ported).</param>
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
