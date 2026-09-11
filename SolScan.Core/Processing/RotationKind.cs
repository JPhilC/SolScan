// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/params/RotationKind.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>Rotation applied to the reconstructed image - direct port of astro4j's
/// <c>RotationKind</c> enum. There's genuinely no 180° option - only left/right quarter-turns.</summary>
public enum RotationKind
{
    None,
    Left,
    Right,
}
