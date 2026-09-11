// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/params/GeometryParams.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>
/// Geometry adjustments applied to the reconstructed image - a deliberately trimmed slice of
/// astro4j's own (much larger) <c>GeometryParams</c> class. Excluded, all Advanced-tab/refinement
/// territory not needed for a basic image to mean something:
/// <list type="bullet">
/// <item><c>tilt</c>/<c>xyRatio</c> - forced overrides for when auto ellipse-fitting gets it wrong;
/// SolScan has no ellipse-fitting/auto-detection yet either, so exposing a manual override for it now
/// would be premature - comes back once that's real.</item>
/// <item><c>disallowDownsampling</c>, <c>autocorrectAngleP</c>, <c>forcePolynomial</c>/
/// <c>forcedPolynomial</c>, <c>saturatedDiskMode</c>, <c>referencePolynomialDirectory</c>,
/// <c>ellipseFittingMode</c>, <c>horizontalFlipCondition</c>/<c>verticalFlipCondition</c> - all
/// Advanced-tab territory.</item>
/// <item><c>spectrumVFlip</c> - SolScan already captures this at the equipment level
/// (<see cref="SolScan.Core.Equipment.SpectrographProfile.SpectrumVFlip"/>); a future pipeline reads
/// it from there rather than duplicating it as a process parameter.</item>
/// </list>
/// </summary>
public sealed record GeometryParams(
    RotationKind Rotation,
    AutocropMode AutocropMode,
    int? FixedWidth,
    bool HorizontalMirror,
    bool VerticalMirror)
{
    public static GeometryParams Default { get; } =
        new(RotationKind.None, AutocropMode.Off, null, false, false);
}
