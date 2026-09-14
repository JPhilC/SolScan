// Adapted from astro4j's jsolex-core/src/main/java/me/champeau/a4j/jsolex/processing/params/RequestedImages.java
// (Apache License, Version 2.0: http://www.apache.org/licenses/LICENSE-2.0). See SolScan's NOTICE
// file for full attribution.

namespace SolScan.Core.Processing;

/// <summary>
/// Which built-in image kinds to generate from a processed capture - a flat checklist over every
/// <see cref="GeneratedImageKind"/> SolScan v1 supports (see that enum's own doc comment for why it
/// doesn't carry forward JSolex's own Basic/Advanced Images split). Astro4j's own <c>RequestedImages</c>
/// also carries pixel-shift bookkeeping and ImageMath script wiring, both out of scope until there's a
/// use for them.
/// </summary>
/// <param name="Images">A concrete <see cref="HashSet{T}"/>, not <see cref="IReadOnlySet{T}"/> -
/// <c>System.Text.Json</c> needs a concrete collection type to deserialize into.</param>
public sealed record RequestedImages(HashSet<GeneratedImageKind> Images)
{
    public bool IsEnabled(GeneratedImageKind kind) => Images.Contains(kind);

    /// <summary>Every currently-ported kind selected - mirrors astro4j's own out-of-the-box default
    /// (<c>RequestedImages.FULL_MODE</c>, everything except Debug Options/scripts not yet ported)
    /// scoped down to what SolScan actually declares today.</summary>
    public static RequestedImages Default { get; } = new([
        GeneratedImageKind.Raw,
        GeneratedImageKind.Reconstruction,
        GeneratedImageKind.Continuum,
        GeneratedImageKind.GeometryCorrected,
        GeneratedImageKind.GeometryCorrectedProcessed,
        GeneratedImageKind.Colorized,
        GeneratedImageKind.VirtualEclipse,
    ]);

    // Records auto-generate Equals/GetHashCode that compare the Images field via
    // EqualityComparer<HashSet<T>>.Default - which, since HashSet<T> doesn't override Equals itself,
    // falls back to reference equality. That would make two RequestedImages with identical selected
    // kinds (e.g. one loaded fresh from JSON, one built in memory) compare unequal, so these replace
    // the compiler-generated members with real set-content equality instead.
    public bool Equals(RequestedImages? other) => other is not null && Images.SetEquals(other.Images);

    public override int GetHashCode()
    {
        var hash = 0;
        foreach (var kind in Images)
        {
            hash ^= kind.GetHashCode();
        }

        return hash;
    }
}
