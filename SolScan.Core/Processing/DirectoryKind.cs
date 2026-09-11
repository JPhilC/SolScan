// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/sun/workflow/DirectoryKind.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>Which output subfolder a <see cref="GeneratedImageKind"/> belongs in - see
/// <see cref="GeneratedImageKindExtensions.GetDirectoryKind"/> for the per-kind mapping and
/// <see cref="ProcessingLocations.GetImagesFolder"/> for how this becomes an actual path. The folder
/// name is this value's own name lower-cased (<c>Raw</c> -&gt; "raw", etc.) - same convention
/// <c>NamingStrategyAwareImageEmitter.java</c> uses (<c>kind.directoryKind().name().toLowerCase(...)</c>).
/// Only <see cref="Raw"/>/<see cref="Processed"/> are actually used by anything SolScan generates
/// today - <see cref="Debug"/>/<see cref="Custom"/> are carried for parity with the original enum,
/// ready for when Debug Options/ImageMath scripts are ported.</summary>
public enum DirectoryKind
{
    Raw,
    Processed,
    Custom,
    Debug,
}
