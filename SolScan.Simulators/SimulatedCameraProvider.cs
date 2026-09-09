using SolScan.Core.Camera;

namespace SolScan.Simulators;

/// <summary>Always reports exactly one <see cref="SimulatedCameraDevice"/> - guarantees the
/// Capture view's camera picker never comes back empty, even with zero real hardware attached.</summary>
public sealed class SimulatedCameraProvider : ICameraProvider
{
    public string VendorName => "Simulated";

    public IReadOnlyList<ICameraDevice> Discover() => [new SimulatedCameraDevice()];
}
