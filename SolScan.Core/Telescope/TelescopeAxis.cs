namespace SolScan.Core.Telescope;

/// <summary>
/// Which mount axis to drive - matches ASCOM's own TelescopeAxes numeric encoding (0/1/2) used by
/// Alpaca's <c>moveaxis</c>/<c>axisrates</c> endpoints. No Tertiary - not needed for an
/// equatorial-only mount (see SolScan CLAUDE.md "Scope for v1").
/// </summary>
public enum TelescopeAxis
{
    /// <summary>Right ascension.</summary>
    Primary = 0,

    /// <summary>Declination.</summary>
    Secondary = 1,
}
