using System.Runtime.InteropServices;

namespace SolScan.Infrastructure.Camera.Asi;

/// <summary>
/// Hand-written P/Invoke subset against ZWO's public ASICamera2 SDK ABI (ASICamera2.dll, x64 -
/// not shipped here, see SolScan.Infrastructure/ASICamera2.README.md) - deliberately not a port of
/// any third-party source: unlike a ToupTek-alike vendor, ZWO ships only a C header
/// (ASICamera2.h), no ready C# wrapper, so there's nothing to adapt from - this is written
/// directly against that header's public, documented function/struct layout. Only the
/// video-capture function family is declared, matching
/// <see cref="Camera.ICameraDevice"/>'s existing streaming-only scope (see its doc comment) - the
/// single-exposure family (ASIStartExposure/ASIGetDataAfterExp) is deliberately omitted.
///
/// <see cref="AsiCameraInfo"/>'s field layout is written from the publicly documented header and
/// not verified against real hardware in this environment - if a real ASI camera's info/frames
/// come back garbled, check this struct's field order/sizes against the ASICamera2.h shipped in
/// ZWO's own SDK download first.
/// </summary>
internal static class AsiNative
{
    private const string DllName = "ASICamera2";

    internal enum AsiErrorCode
    {
        Success = 0,
        InvalidIndex,
        InvalidId,
        InvalidControlType,
        CameraClosed,
        CameraRemoved,
        InvalidPath,
        InvalidFileFormat,
        InvalidSize,
        InvalidImageType,
        OutOfBoundary,
        Timeout,
        InvalidSequence,
        BufferTooSmall,
        VideoModeActive,
        ExposureInProgress,
        GeneralError,
        InvalidMode,
    }

    internal enum AsiControlType
    {
        Gain = 0,
        Exposure = 1,
        BandwidthOverload = 6,
    }

    /// <summary>Only the mono formats SolScan's target hardware (ASI678MM and similar mono
    /// cameras) uses - RGB24/Y8 (color/binned-mono) are out of scope for v1, see CLAUDE.md.</summary>
    internal enum AsiImageType
    {
        Raw8 = 0,
        Raw16 = 2,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct AsiCameraInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Name;

        public int CameraId;
        public int MaxHeight;
        public int MaxWidth;
        public int IsColorCam;
        public int BayerPattern;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public int[] SupportedBins;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public int[] SupportedVideoFormat;

        public double PixelSize;
        public int MechanicalShutter;
        public int St4Port;
        public int IsCoolerCam;
        public int IsUsb3Host;
        public int IsUsb3Camera;
        public float ElecPerAdu;
        public int BitDepth;
        public int IsTriggerCam;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Unused;
    }

    [DllImport(DllName, EntryPoint = "ASIGetNumOfConnectedCameras", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetNumOfConnectedCameras();

    [DllImport(DllName, EntryPoint = "ASIGetCameraProperty", CallingConvention = CallingConvention.Cdecl)]
    internal static extern AsiErrorCode GetCameraProperty(out AsiCameraInfo cameraInfo, int cameraIndex);

    [DllImport(DllName, EntryPoint = "ASIOpenCamera", CallingConvention = CallingConvention.Cdecl)]
    internal static extern AsiErrorCode OpenCamera(int cameraId);

    [DllImport(DllName, EntryPoint = "ASIInitCamera", CallingConvention = CallingConvention.Cdecl)]
    internal static extern AsiErrorCode InitCamera(int cameraId);

    [DllImport(DllName, EntryPoint = "ASICloseCamera", CallingConvention = CallingConvention.Cdecl)]
    internal static extern AsiErrorCode CloseCamera(int cameraId);

    [DllImport(DllName, EntryPoint = "ASISetControlValue", CallingConvention = CallingConvention.Cdecl)]
    internal static extern AsiErrorCode SetControlValue(int cameraId, AsiControlType controlType, int value, int isAuto);

    [DllImport(DllName, EntryPoint = "ASIGetControlValue", CallingConvention = CallingConvention.Cdecl)]
    internal static extern AsiErrorCode GetControlValue(int cameraId, AsiControlType controlType, out int value, out int isAuto);

    [DllImport(DllName, EntryPoint = "ASISetROIFormat", CallingConvention = CallingConvention.Cdecl)]
    internal static extern AsiErrorCode SetRoiFormat(int cameraId, int width, int height, int binning, AsiImageType imageType);

    [DllImport(DllName, EntryPoint = "ASIGetDroppedFrames", CallingConvention = CallingConvention.Cdecl)]
    internal static extern AsiErrorCode GetDroppedFrames(int cameraId, out int droppedFrames);

    [DllImport(DllName, EntryPoint = "ASIStartVideoCapture", CallingConvention = CallingConvention.Cdecl)]
    internal static extern AsiErrorCode StartVideoCapture(int cameraId);

    [DllImport(DllName, EntryPoint = "ASIStopVideoCapture", CallingConvention = CallingConvention.Cdecl)]
    internal static extern AsiErrorCode StopVideoCapture(int cameraId);

    [DllImport(DllName, EntryPoint = "ASIGetVideoData", CallingConvention = CallingConvention.Cdecl)]
    internal static extern AsiErrorCode GetVideoData(int cameraId, [Out] byte[] buffer, int bufferSize, int waitMs);
}
