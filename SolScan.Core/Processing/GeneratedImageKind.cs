// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/sun/workflow/GeneratedImageKind.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>
/// Which built-in output image kind can be generated from a processed SER capture - ported from
/// astro4j's <c>GeneratedImageKind</c> enum. Originally scoped to just the "Basic Images" values
/// JSolex's "Image Selection and Scripts" tab exposes in its own Basic Images section;
/// <see cref="Colorized"/> is the first value ported from that tab's separate Advanced Images
/// section (astro4j's own <c>ImageSelectionPanel.java</c> puts its checkbox in <c>advancedGrid</c>,
/// not the basic one) - the rest of Advanced Images (Doppler, redshift, active regions, ...) and
/// Debug Options still add more values here once those sections land - see SolScan CLAUDE.md.
/// </summary>
public enum GeneratedImageKind
{
    /// <summary>The unprocessed reconstructed disk image at the studied line's pixel shift.</summary>
    Raw,

    /// <summary>The progressive reconstruction, built up scan-line by scan-line as the capture is
    /// read.</summary>
    Reconstruction,

    /// <summary>The disk reconstructed at the continuum pixel shift (<see cref="SpectrumParams.ContinuumShift"/>).</summary>
    Continuum,

    /// <summary>The raw reconstruction with geometry correction (tilt/ellipse/rotation/crop)
    /// applied, but no contrast enhancement.</summary>
    GeometryCorrected,

    /// <summary>The geometry-corrected image with contrast enhancement applied (see
    /// <see cref="ContrastEnhancementMode"/>).</summary>
    GeometryCorrectedProcessed,

    /// <summary>The geometry-corrected-and-enhanced image tinted to approximate the studied
    /// <see cref="SpectrumParams.Ray"/>'s real colour (a fixed <see cref="SpectralRay.ColorCurve"/>
    /// for H-alpha, otherwise <see cref="SpectralRay.ToRgb"/>'s wavelength approximation) - not
    /// produced at all when <see cref="SpectrumParams.Ray"/> is <see cref="SpectralRay.Other"/>,
    /// which has no wavelength to derive a colour from.</summary>
    Colorized,
}

/// <summary>The <see cref="DirectoryKind.GetDirectoryKind"/> extension - split into its own static
/// class since C# enums can't carry the per-value associated data astro4j's own enum constructor
/// does (<c>RAW(DisplayCategory.RAW, DirectoryKind.RAW)</c> etc. in <c>GeneratedImageKind.java</c>).</summary>
public static class GeneratedImageKindExtensions
{
    /// <summary>Which output subfolder this kind's images belong in - direct port of
    /// <c>GeneratedImageKind.java</c>'s own per-constant <c>directoryKind()</c> mapping. Notably,
    /// <see cref="GeneratedImageKind.Continuum"/> maps to <see cref="DirectoryKind.Processed"/>, not
    /// <see cref="DirectoryKind.Raw"/>, even though it isn't contrast-enhanced - the original's real
    /// distinction is "the literal at-line-centre reconstruction and its progressive-display twin"
    /// (Raw) vs. "everything else, including other pixel shifts and geometry operations" (Processed),
    /// not "unstretched vs. stretched".</summary>
    public static DirectoryKind GetDirectoryKind(this GeneratedImageKind kind) => kind switch
    {
        GeneratedImageKind.Raw => DirectoryKind.Raw,
        GeneratedImageKind.Reconstruction => DirectoryKind.Raw,
        GeneratedImageKind.Continuum => DirectoryKind.Processed,
        GeneratedImageKind.GeometryCorrected => DirectoryKind.Processed,
        GeneratedImageKind.GeometryCorrectedProcessed => DirectoryKind.Processed,
        GeneratedImageKind.Colorized => DirectoryKind.Processed,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped GeneratedImageKind - add it to GetDirectoryKind."),
    };
}
