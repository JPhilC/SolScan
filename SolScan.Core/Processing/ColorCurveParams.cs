// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/color/ColorCurve.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution. Only the six tunable input/output points are ported here as plain data -
// the per-channel quadratic-fit math that turns them into a mono->RGB mapping is a real algorithm, not
// a domain record, so it lives in SolScan.Processing.Color.ColorCurve instead (Core has no dependency
// on Processing - see CLAUDE.md's "Project layering").

namespace SolScan.Core.Processing;

/// <summary>
/// A per-channel colorization curve: for each of red/green/blue, a quadratic curve through
/// <c>(0, 0)</c>, <c>(In, Out)</c> and <c>(255, 255)</c> (both points expressed on a 0-255 scale, the
/// same scale astro4j's own <c>ColorCurve</c> constructor takes) maps a mono pixel value onto that
/// channel. Only <see cref="SpectralRay.HAlpha"/> has one, matching astro4j's own
/// <c>SpectralRay</c> - every other predefined ray falls back to <see cref="SpectralRay.ToRgb"/>'s
/// wavelength-approximated colour instead (see that method's own doc comment).
/// </summary>
public sealed record ColorCurveParams(int RIn, int ROut, int GIn, int GOut, int BIn, int BOut)
{
    /// <summary>Retuned from astro4j's own <c>KnownCurves.H_ALPHA</c> (<c>84,139, 95,20, 218,65</c>) -
    /// the only named curve actually reachable from a predefined <see cref="SpectralRay"/> (its sibling
    /// <c>CALCIUM</c>/<c>HELIUM</c> curves are unused dead code even in the original, so they aren't
    /// ported - see CLAUDE.md's own "confirmed dead code isn't faithfully reproduced" precedent).
    /// astro4j's own values render a deep crimson red through most of the mid-tone range (green stays
    /// under ~25% of red until close to full white) - the user asked for something more orange, so the
    /// green channel's curve was raised (anchor moved from 95→20 to 60→55, letting green ramp up much
    /// earlier and further) and red/blue nudged slightly (84→150, 220→40) to match - verified by
    /// evaluating the fitted curves directly (not just eyeballing the raw input numbers) across the
    /// full mono range: a smooth, monotonic progression from black through deep orange (~mono 16k),
    /// solid orange (~mono 33k), golden orange (~mono 49k), to a pale near-white highlight - a
    /// deliberate customization, not a faithful port, so this is no longer astro4j's stock curve.</summary>
    public static readonly ColorCurveParams HAlpha = new(84, 150, 60, 55, 220, 40);
}
