// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/params/ProcessParams.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>
/// Top-level processing parameters - mirrors astro4j's own 11-slot <c>ProcessParams</c> record shape,
/// though only 7 of those slots are populated so far (the others - Observation details, Advanced,
/// Format/Output, banding corrections, etc. - land as later increments, at which point this record
/// grows rather than being renamed, same reasoning as
/// <see cref="SolScan.Core.Capture.AppSettings"/>'s own doc comment about optional trailing
/// parameters). Edited directly on the Process view (a right-hand panel of Expanders wrapping
/// <c>ProcessParametersView</c>/<c>ImageEnhancementView</c>/<c>ImageSelectionView</c> - moved there from
/// Options, which used to own this record's editing) but persisted as one record - see
/// <c>ProcessViewModel</c>'s own doc comment for how its three child view models' edits get
/// auto-saved (debounced) into a single call to <see cref="IProcessParamsStore.Save"/>.
/// </summary>
public sealed record ProcessParams(
    RequestedImages RequestedImages,
    SpectrumParams SpectrumParams,
    GeometryParams GeometryParams,
    ContrastEnhancementMode ContrastEnhancement,
    ClaheParams ClaheParams,
    Clahe2Params Clahe2Params,
    AutoStretchParams AutoStretchParams)
{
    public static ProcessParams CreateDefault() => new(
        RequestedImages.Default,
        SpectrumParams.Default,
        GeometryParams.Default,
        ContrastEnhancementMode.Auto,
        ClaheParams.Default,
        Clahe2Params.Default,
        AutoStretchParams.Default);
}

/// <summary>
/// Persists <see cref="ProcessParams"/> - implemented by
/// <c>SolScan.Infrastructure.Processing.JsonProcessParamsStore</c>, one JSON file under
/// <c>%LocalAppData%\SolScan\</c>, same single-record shape as
/// <see cref="SolScan.Core.Capture.IAppSettingsStore"/> (there's nothing to key this by - it's one
/// global set of defaults, same as <c>AppSettings</c>).
/// </summary>
public interface IProcessParamsStore
{
    /// <summary>Never returns null itself - an unset/missing/corrupt file just yields
    /// <see cref="ProcessParams.CreateDefault"/>.</summary>
    ProcessParams Load();

    void Save(ProcessParams processParams);
}
