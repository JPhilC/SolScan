// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/params/ContrastEnhancement.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>Which contrast-enhancement method to apply to a "processed" output image - ported from
/// astro4j's <c>ContrastEnhancement</c> enum (renamed here to avoid a type/property name collision
/// with the <see cref="ProcessParams.ContrastEnhancement"/> field that holds it). The method's own
/// tuning parameters (tile size/bins/clip limit for CLAHE, clip limit for CLAHE2, gamma/background
/// threshold/prominence stretch for AutoStretch) live alongside this choice on <see cref="ProcessParams"/>
/// as <see cref="ClaheParams"/>/<see cref="Clahe2Params"/>/<see cref="AutoStretchParams"/>.</summary>
public enum ContrastEnhancementMode
{
    Auto,
    Clahe,
    Clahe2,
    AutoStretch,
}
