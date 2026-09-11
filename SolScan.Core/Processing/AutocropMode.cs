// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/params/AutocropMode.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>Automatic cropping mode for processed images - direct port of astro4j's
/// <c>AutocropMode</c> enum.</summary>
public enum AutocropMode
{
    /// <summary>No automatic cropping.</summary>
    Off,

    /// <summary>Crop to the source image width.</summary>
    SourceWidth,

    /// <summary>Crop to a 1:1 radius ratio.</summary>
    Radius1To1,

    /// <summary>Crop to a 1:2 radius ratio.</summary>
    Radius1To2,

    /// <summary>Crop to a 1:5 radius ratio.</summary>
    Radius1To5,

    /// <summary>Crop to a fixed width (see <see cref="GeometryParams.FixedWidth"/>).</summary>
    FixedWidth,
}
