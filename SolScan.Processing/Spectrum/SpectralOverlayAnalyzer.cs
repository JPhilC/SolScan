using SolScan.Core.Camera;
using SolScan.Core.Equipment;
using SolScan.Core.Processing;
using SolScan.Processing.Shg;

namespace SolScan.Processing.Spectrum;

/// <summary>
/// Which of the 12 named <see cref="SpectralRay"/> lines a single live camera frame currently shows,
/// and where each one sits - the live-preview counterpart to <c>SolScan.Tools</c>' <c>annotate</c>
/// command, which runs the same identification core (<see cref="SpectralLineCurvatureDetector"/> →
/// <see cref="SpectralProfileExtractor"/> → <see cref="SpectralLineIdentifier"/>) against a whole
/// recorded file's own <see cref="FrameAverager"/>-cleaned average instead. A live overlay can't wait
/// for a whole recording, so this runs on one frame at a time - noisier than the averaged data every
/// prior validation used (see CLAUDE.md), a real, acknowledged trade-off rather than an oversight.
/// </summary>
public static class SpectralOverlayAnalyzer
{
    /// <param name="frame">A single live camera frame - not averaged.</param>
    /// <param name="instrument">The connected SHG's own optics.</param>
    /// <param name="pixelSizeMicrons">The connected camera's own pixel size.</param>
    /// <param name="binning">Pixel binning currently in effect.</param>
    /// <param name="maxShiftPixels">How far above/below the fitted line's own centre row
    /// <see cref="SpectralProfileExtractor"/> samples - typically half the frame's own height, same
    /// as <c>SolScan.Tools</c>' own <c>annotate</c> command.</param>
    /// <param name="referenceWindows">Defaults to the bundled resource - overridable so tests (and any
    /// future caller with its own reference data) can supply synthetic windows instead.</param>
    public static SpectralOverlayResult Analyze(
        CameraFrame frame,
        SpectrographProfile instrument,
        double pixelSizeMicrons,
        int binning = 1,
        int? maxShiftPixels = null,
        IReadOnlyList<ReferenceWindow>? referenceWindows = null)
    {
        var floatFrame = FrameConversion.ToFloatArray(frame);
        var polynomial = new SpectralLineCurvatureDetector().Detect(floatFrame);
        // A single representative row for the whole line, at the frame's own horizontal centre - the
        // fitted curve's row position varies slightly across columns (the "smile"), but a live overlay
        // places one label per line, not one per column, so this is the one Y a caller needs.
        var centreRowInFrame = polynomial.Evaluate(frame.Width / 2.0);

        var shiftLimit = maxShiftPixels ?? (System.Math.Max(1, frame.Height / 2) - 1);
        var profile = SpectralProfileExtractor.Extract(floatFrame, polynomial, shiftLimit);

        var identifier = new SpectralLineIdentifier(instrument, pixelSizeMicrons, binning, referenceWindows);
        var identification = identifier.Identify(profile);

        // The winning (best-guess) candidate anchors the layout even when not confident - see
        // SpectralOverlayResult's own doc comment for why: the caller decides how to render an
        // unconfident anchor (e.g. dimmed), this type doesn't withhold the layout entirely.
        var anchor = identification.AllCandidates.Count > 0 ? identification.AllCandidates[0].Ray : null;
        if (anchor is null)
        {
            return new SpectralOverlayResult(identification, [], null, centreRowInFrame);
        }

        var anchorDispersion = SpectralDispersion.ComputeAngstromsPerPixel(instrument, anchor.WavelengthAngstroms, pixelSizeMicrons, binning);

        var visibleLines = new List<VisibleSpectralLine>();
        foreach (var ray in SpectralRay.Predefined)
        {
            if (ray.WavelengthAngstroms <= 0)
            {
                continue; // SpectralRay.Other - not a real line to place.
            }

            var pixelShift = (ray.WavelengthAngstroms - anchor.WavelengthAngstroms) / anchorDispersion;
            if (pixelShift >= -shiftLimit && pixelShift <= shiftLimit)
            {
                visibleLines.Add(new VisibleSpectralLine(ray, pixelShift, centreRowInFrame + pixelShift));
            }
        }

        return new SpectralOverlayResult(identification, visibleLines, anchorDispersion, centreRowInFrame);
    }
}

/// <param name="Ray">The named line.</param>
/// <param name="PixelShiftFromCentre">Its position, in pixel-shift from the frame's own detected line
/// centre (<see cref="SpectralProfile"/>'s own shift-0 row) - the same units a caller maps onto an
/// on-screen row the same way <see cref="SpectralProfileExtractor"/>'s own caller already does.</param>
/// <param name="RowInFrame">The same position, already resolved to an actual raw-frame row (
/// <see cref="SpectralOverlayResult.CentreRowInFrame"/> + <see cref="PixelShiftFromCentre"/>) - what a
/// UI caller actually wants, so it doesn't need to separately track the frame's own detected centre row.</param>
public sealed record VisibleSpectralLine(SpectralRay Ray, double PixelShiftFromCentre, double RowInFrame);

/// <param name="Identification">The full identification result (winning ray, confidence, every
/// candidate's score) - <see cref="SpectralLineIdentificationResult.IdentifiedRay"/> is null exactly
/// when the winning candidate didn't clear the confidence gate; the winning candidate itself is
/// still <see cref="SpectralLineIdentificationResult.AllCandidates"/>'s first entry either way, and
/// is what <see cref="VisibleLines"/> is anchored to - a caller wanting to distinguish "confident" from
/// "best guess only" compares its own line against <c>Identification.IdentifiedRay</c>.</param>
/// <param name="VisibleLines">Every named line whose projected position falls within the frame's own
/// sampled pixel-shift range - empty if no candidate could be scored at all (e.g. a degenerate/blank
/// frame with no reference windows matching).</param>
/// <param name="AnchorDispersionAngstromsPerPixel">Å/pixel at the anchor's own wavelength - null
/// exactly when <see cref="VisibleLines"/> is empty for the same reason. Useful to a caller (e.g. the
/// colour-gradient band) that needs to convert the visible pixel range into a wavelength range without
/// recomputing dispersion per ray itself.</param>
/// <param name="CentreRowInFrame">The fitted line-curvature polynomial evaluated at the frame's own
/// horizontal centre - always present (even with no scored candidates at all), since it only depends
/// on the curvature fit, not identification.</param>
public sealed record SpectralOverlayResult(
    SpectralLineIdentificationResult Identification,
    IReadOnlyList<VisibleSpectralLine> VisibleLines,
    double? AnchorDispersionAngstromsPerPixel,
    double CentreRowInFrame);
