// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/sun/tasks/CoronagraphTask.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE file
// for full attribution. The original constructor also takes a `blackPoint` - confirmed dead in
// astro4j itself (stored on the instance but never read by `doCall`), so it's dropped here, matching
// this project's established "confirmed dead code isn't faithfully reproduced" precedent (see e.g.
// DiskEdgeDetector's/ArcsinhStretchingStrategy's own header comments).

using SolScan.Processing.Math;
using SolScan.Processing.Stretching;

namespace SolScan.Processing.Shg;

/// <summary>Produces <see cref="SolScan.Core.Processing.GeneratedImageKind.VirtualEclipse"/> ("virtual
/// eclipse"/coronagraph image): blanks the solar disk itself, then neutralizes and arcsinh-stretches
/// whatever surrounds it, so faint prominences/streamers near the limb become visible the way they'd
/// look during a real eclipse - the disk's own overwhelming brightness is what normally buries them.
/// </summary>
public static class Coronagraph
{
    private const double MaxPixelValue = 65535;

    /// <summary>Runs on a private copy of <paramref name="geometryCorrectedPixels"/> - the
    /// geometry-corrected (not yet contrast-enhanced) image, scaled to the 0-65535 container range
    /// every stretching algorithm here assumes - and returns a new buffer in that same range, already
    /// clamped/renormalized by the final arcsinh stretch.</summary>
    public static float[,] Produce(float[,] geometryCorrectedPixels, Ellipse ellipse)
    {
        var work = (float[,])geometryCorrectedPixels.Clone();
        DiskFill.FillWithGradient(ellipse, work, 0f);

        // Two rounds of ellipse-aware blind background neutralization, matching astro4j's own
        // `for (i = 0; i < 2; i++) work = BackgroundRemoval.neutralizeBackground(work)`.
        for (var i = 0; i < 2; i++)
        {
            work = BackgroundNeutralizer.BlindNeutralize(work, MaxPixelValue, ellipse).Neutralized;
        }

        new ArcsinhStretchingStrategy(0, 50).Stretch(work);
        return work;
    }
}
