using SolScan.Core.Camera;
using static SolScan.Infrastructure.Camera.Altair.AltairNative;

namespace SolScan.Infrastructure.Camera.Altair;

/// <summary>ICameraProvider for Altair cameras. Discover() must survive altaircam.dll not being
/// present at all (no Altair camera ever attached to this machine) - see
/// SolScan.External/x64/Altair/README.md.</summary>
public sealed class AltairCameraProvider : ICameraProvider
{
    public string VendorName => "Altair";

    public IReadOnlyList<ICameraDevice> Discover()
    {
        try
        {
            var devices = new DeviceInfo[MaxDevices];
            var count = EnumV2(devices);
            var result = new List<ICameraDevice>((int)count);
            for (var i = 0; i < count; i++)
            {
                result.Add(new AltairCameraDevice(devices[i].Id, devices[i].DisplayName));
            }

            return result;
        }
        catch (DllNotFoundException)
        {
            // No altaircam.dll on this machine - not an error, just nothing to discover.
            return [];
        }
    }
}
