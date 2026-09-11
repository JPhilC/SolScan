using SolScan.Core.Camera;

namespace SolScan.Core.Capture;

/// <summary>
/// The 178-byte SER header, parsed as-is - the byte-for-byte inverse of what <see cref="ISerWriter.Open"/>
/// writes: 14-byte ASCII FileID, then LuID/ColorID/LittleEndian/Width/Height/PixelDepth/FrameCount as
/// 4-byte ints, three 40-byte ASCII fields (Observer/Instrument/Telescope), then two 8-byte .NET-ticks
/// timestamps (local, then UTC). <see cref="PixelDepth"/> is whatever the file actually declares (e.g.
/// 14 for the ASI-style "14-bit ADC packed into a 16-bit container" layout some SHG cameras use, not
/// necessarily exactly 8 or 16) - callers doing bytes-per-pixel math should use the same
/// <c>PixelDepth &lt;= 8 ? 1 : 2</c> rule <see cref="ISerWriter"/>/AsiCameraDevice already use, not
/// assume a specific depth. <see cref="ColorId"/> reflects how pixels are stored in this particular
/// file, not the physical camera sensor - a colour sensor's SHG capture can still be written out as
/// plain mono (ColorID 0) if the capturing app already reduced it to one plane.
/// </summary>
public sealed record SerHeader(
    string FileId,
    int ColorId,
    bool LittleEndian,
    int Width,
    int Height,
    int PixelDepth,
    int FrameCount,
    string Observer,
    string Instrument,
    string Telescope,
    DateTime DateTimeLocal,
    DateTime DateTimeUtc);

/// <summary>
/// Reads a .ser video file written in the same "LUCAM-RECORDER" layout <see cref="ISerWriter"/> writes
/// (and that FireCapture/SharpCap/PIPP/JSolex/Sunscan all produce) - the read-side counterpart, used by
/// SolScan.Processing to open a finished capture rather than a live camera stream. Only single-plane
/// (mono) pixel layouts are handled - no Bayer/RGB-plane unpacking.
/// </summary>
public interface ISerReader : IDisposable
{
    /// <summary>Parsed by <see cref="Open"/>; throws if accessed beforehand.</summary>
    SerHeader Header { get; }

    void Open(string path);

    /// <summary>Reads one frame's raw pixel bytes by seeking directly to its offset - frames aren't
    /// required to be read in order. <paramref name="index"/> is 0-based, less than
    /// <see cref="SerHeader.FrameCount"/>. The returned frame's <c>TimestampUtc</c> comes from the
    /// file's per-frame trailer when present; falls back to <see cref="DateTime.MinValue"/> for a file
    /// too short to carry one (real-world files not written by <see cref="ISerWriter"/> - e.g. some
    /// Sunscan captures - may omit it entirely).</summary>
    CameraFrame ReadFrame(int index);

    void Close();
}
