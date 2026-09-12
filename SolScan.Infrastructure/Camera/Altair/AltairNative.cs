using System.Runtime.InteropServices;

namespace SolScan.Infrastructure.Camera.Altair;

/// <summary>
/// Hand-written P/Invoke subset against Altair's native SDK (altaircam.dll, x64 - not shipped
/// here, see SolScan.External/x64/Altair/README.md). Altair cameras share the same
/// "ToupTek-alike" native ABI as
/// ToupTek/OGMA/Levenhuk (confirmed by reading N.I.N.A.'s own Altair adapter - see CLAUDE.md's
/// N.I.N.A. entry) - deliberately hand-written rather than copying that vendor SDK file out of
/// N.I.N.A.'s repo, since that copy has N.I.N.A.-specific edits baked in and this ABI is small,
/// stable and independently documented (every open-source driver for these cameras - INDI, PHD2,
/// ASCOM - has its own independent binding for exactly this reason). Only what
/// <see cref="AltairCameraDevice"/> needs is declared: enumeration, open/close, exposure/gain/
/// speed get-set, and pull-mode video capture. <c>OPTION_RAW</c>/<c>OPTION_BITDEPTH</c>'s values
/// were confirmed directly against N.I.N.A.'s vendor-sourced altaircam.cs; everything else here
/// (struct field order/sizes in particular) is reconstructed from general knowledge of this ABI
/// and NOT verified against real Altair hardware or the vendor's own header in this environment -
/// if enumeration or frames come back garbled on real hardware, check this file's structs first
/// against the altaircam.h shipped in Altair's own SDK download.
/// </summary>
internal static class AltairNative
{
    private const string DllName = "altaircam";

    /// <summary>Vendor-documented hard cap on simultaneously enumerated cameras - the array
    /// passed to <see cref="EnumV2"/> must be at least this large, since the native call has no
    /// way to know our array's length and writes as many devices as it finds.</summary>
    internal const int MaxDevices = 128;

    internal enum Event : uint
    {
        /// <summary>A new frame is ready to be pulled via <see cref="PullImageV3"/> - the only
        /// event this app's pull-mode loop reacts to.</summary>
        Image = 0x0004,
        StillImage = 0x0005,
        Error = 0x80,
        Disconnected = 0x81,
    }

    /// <summary>Only the three options SolScan needs - raw (undemosaiced) sensor data, its bit
    /// depth, and binning - all three confirmed against N.I.N.A.'s vendor-sourced altaircam.cs
    /// (Binning's encoding is genuinely odd - see <see cref="AltairCameraDevice"/>'s remarks on it,
    /// this isn't a plain 1-4 factor like ASI's).</summary>
    internal enum Option : uint
    {
        Raw = 0x04,
        BitDepth = 0x06,
        Binning = 0x17,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DeviceInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Id;

        /// <summary>Pointer to the vendor's model-info struct (resolution list, max speed, pixel
        /// size, ...) - deliberately left undereferenced. Declaring it (rather than omitting it)
        /// keeps this struct's size/stride matching the native array element size; everything
        /// <see cref="AltairCameraDevice"/> needs is queried from the open handle instead.</summary>
        public IntPtr Model;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FrameInfo
    {
        public uint Width;
        public uint Height;
        public uint Flag;
        public uint Sequence;
        public ulong TimestampMicroseconds;
        public uint ShutterSequence;
        public uint ExposureTime;
        public ushort ExposureGain;
        public ushort BlackLevel;
    }

    internal delegate void EventCallback(uint nEvent, IntPtr ctxCallback);

    [DllImport(DllName, EntryPoint = "Altaircam_EnumV2", CallingConvention = CallingConvention.Winapi)]
    internal static extern uint EnumV2([Out] DeviceInfo[] devices);

    [DllImport(DllName, EntryPoint = "Altaircam_Open", CallingConvention = CallingConvention.Winapi)]
    internal static extern IntPtr Open([MarshalAs(UnmanagedType.LPWStr)] string? id);

    [DllImport(DllName, EntryPoint = "Altaircam_Close", CallingConvention = CallingConvention.Winapi)]
    internal static extern void Close(IntPtr h);

    [DllImport(DllName, EntryPoint = "Altaircam_get_Size", CallingConvention = CallingConvention.Winapi)]
    internal static extern int GetSize(IntPtr h, out int width, out int height);

    [DllImport(DllName, EntryPoint = "Altaircam_put_ExpoTime", CallingConvention = CallingConvention.Winapi)]
    internal static extern int PutExpoTime(IntPtr h, uint timeMicroseconds);

    [DllImport(DllName, EntryPoint = "Altaircam_get_ExpoTime", CallingConvention = CallingConvention.Winapi)]
    internal static extern int GetExpoTime(IntPtr h, out uint timeMicroseconds);

    [DllImport(DllName, EntryPoint = "Altaircam_put_ExpoAGain", CallingConvention = CallingConvention.Winapi)]
    internal static extern int PutExpoAGain(IntPtr h, ushort gain);

    [DllImport(DllName, EntryPoint = "Altaircam_get_ExpoAGain", CallingConvention = CallingConvention.Winapi)]
    internal static extern int GetExpoAGain(IntPtr h, out ushort gain);

    /// <summary>ToupTek-alike cameras have one auto-exposure flag governing exposure time and gain
    /// together (no independent auto-gain-only control) - see <see cref="AltairCameraDevice"/>.</summary>
    [DllImport(DllName, EntryPoint = "Altaircam_put_AutoExpoEnable", CallingConvention = CallingConvention.Winapi)]
    internal static extern int PutAutoExpoEnable(IntPtr h, int isAuto);

    [DllImport(DllName, EntryPoint = "Altaircam_get_AutoExpoEnable", CallingConvention = CallingConvention.Winapi)]
    internal static extern int GetAutoExpoEnable(IntPtr h, out int isAuto);

    [DllImport(DllName, EntryPoint = "Altaircam_put_Speed", CallingConvention = CallingConvention.Winapi)]
    internal static extern int PutSpeed(IntPtr h, ushort speed);

    [DllImport(DllName, EntryPoint = "Altaircam_get_Speed", CallingConvention = CallingConvention.Winapi)]
    internal static extern int GetSpeed(IntPtr h, out ushort speed);

    [DllImport(DllName, EntryPoint = "Altaircam_get_MaxSpeed", CallingConvention = CallingConvention.Winapi)]
    internal static extern uint GetMaxSpeed(IntPtr h);

    [DllImport(DllName, EntryPoint = "Altaircam_put_Option", CallingConvention = CallingConvention.Winapi)]
    internal static extern int PutOption(IntPtr h, Option option, int value);

    [DllImport(DllName, EntryPoint = "Altaircam_get_Option", CallingConvention = CallingConvention.Winapi)]
    internal static extern int GetOption(IntPtr h, Option option, out int value);

    [DllImport(DllName, EntryPoint = "Altaircam_StartPullModeWithCallback", CallingConvention = CallingConvention.Winapi)]
    internal static extern int StartPullModeWithCallback(IntPtr h, EventCallback funCallback, IntPtr ctxCallback);

    [DllImport(DllName, EntryPoint = "Altaircam_Stop", CallingConvention = CallingConvention.Winapi)]
    internal static extern int Stop(IntPtr h);

    [DllImport(DllName, EntryPoint = "Altaircam_PullImageV3", CallingConvention = CallingConvention.Winapi)]
    internal static extern int PullImageV3(IntPtr h, [Out] byte[] imageData, int bits, int rowPitch, out FrameInfo info);
}
