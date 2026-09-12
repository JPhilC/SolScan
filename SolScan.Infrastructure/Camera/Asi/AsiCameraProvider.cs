using SolScan.Core.Camera;
using static SolScan.Infrastructure.Camera.Asi.AsiNative;

namespace SolScan.Infrastructure.Camera.Asi;

/// <summary>ICameraProvider for ZWO ASI cameras. Discover() must survive ASICamera2.dll not being
/// present at all (no ZWO camera ever attached to this machine) - see
/// SolScan.External/x64/ASI/README.md.</summary>
public sealed class AsiCameraProvider : ICameraProvider
{
    public string VendorName => "ZWO ASI";

    public IReadOnlyList<ICameraDevice> Discover()
    {
        try
        {
            var count = GetNumOfConnectedCameras();
            var devices = new List<ICameraDevice>(count);
            for (var i = 0; i < count; i++)
            {
                if (GetCameraProperty(out var info, i) == AsiErrorCode.Success)
                {
                    devices.Add(new AsiCameraDevice(info.CameraId, info.Name, ReadSupportedBinning(info)));
                }
            }

            return devices;
        }
        catch (DllNotFoundException)
        {
            // No ASICamera2.dll on this machine - not an error, just nothing to discover.
            return [];
        }
    }

    /// <summary>SupportedBins is a fixed-size native array (see <see cref="AsiNative.AsiCameraInfo"/>)
    /// zero-terminated after however many binning factors the camera actually supports (e.g.
    /// [1, 2, 3, 4, 0, 0, ...] for the ASI678MM) - trims off that padding.</summary>
    private static IReadOnlyList<int> ReadSupportedBinning(AsiCameraInfo info) =>
        info.SupportedBins.TakeWhile(bin => bin > 0).ToList();
}
