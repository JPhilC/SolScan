namespace SolScan.Core.Camera;

/// <summary>
/// One per camera vendor (ZWO ASI, Altair, ...; see SolScan.Infrastructure.Camera.Asi/Altair, and
/// SolScan.Simulators for a hardware-free one) - enumerates whatever that vendor's SDK currently
/// sees attached and hands back ready-to-connect <see cref="ICameraDevice"/> instances, mirroring
/// N.I.N.A.'s IEquipmentProvider&lt;ICamera&gt;.GetEquipment() (studied for this shape - see
/// SolScan CLAUDE.md's N.I.N.A. entry).
/// </summary>
public interface ICameraProvider
{
    string VendorName { get; }

    /// <summary>
    /// Enumerates currently attached devices for this vendor. Must never throw for "SDK not
    /// present"/"no devices found" - both are just an empty list, since every registered
    /// provider's Discover() runs on every refresh regardless of whether that vendor's hardware or
    /// native DLL is actually present on this machine.
    /// </summary>
    IReadOnlyList<ICameraDevice> Discover();
}
