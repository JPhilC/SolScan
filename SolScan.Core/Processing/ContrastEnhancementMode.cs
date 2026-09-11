// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/params/ContrastEnhancement.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>Which contrast-enhancement method to apply to a "processed" output image - ported from
/// astro4j's <c>ContrastEnhancement</c> enum (renamed here to avoid a type/property name collision
/// with the <see cref="ProcessParams.ContrastEnhancement"/> field that holds it). Only the method
/// choice itself is carried for now - the CLAHE/AutoStretch tuning parameters each method takes in
/// JSolex (tile size, clip limit, gamma, ...) aren't ported yet; a real pipeline would use sensible
/// built-in defaults for those until then.</summary>
public enum ContrastEnhancementMode
{
    Auto,
    Clahe,
    Clahe2,
    AutoStretch,
}
