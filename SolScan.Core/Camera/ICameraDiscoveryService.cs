namespace SolScan.Core.Camera;

/// <summary>Aggregates every registered <see cref="ICameraProvider"/> into one list for the
/// Capture view's camera picker.</summary>
public interface ICameraDiscoveryService
{
    IReadOnlyList<ICameraDevice> DiscoverAll();
}

/// <summary>
/// Trivial <see cref="ICameraDiscoveryService"/> - just concatenates each provider's
/// <see cref="ICameraProvider.Discover"/>. Lives in Core (rather than Infrastructure) since it has
/// no IO/hardware dependency of its own; the providers it's given do.
/// </summary>
public sealed class CameraDiscoveryService(IEnumerable<ICameraProvider> providers) : ICameraDiscoveryService
{
    public IReadOnlyList<ICameraDevice> DiscoverAll() =>
        providers.SelectMany(p => p.Discover()).ToList();
}
