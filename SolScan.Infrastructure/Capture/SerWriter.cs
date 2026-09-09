using System.Text;
using SolScan.Core.Camera;
using SolScan.Core.Capture;

namespace SolScan.Infrastructure.Capture;

/// <summary>
/// <see cref="ISerWriter"/> writing the standard SER video format (the "LUCAM-RECORDER" layout
/// FireCapture/SharpCap/PIPP/JSolex all read): a 178-byte header, followed by each frame's raw
/// pixel bytes back to back with no padding, followed by a trailer of one 8-byte .NET-ticks UTC
/// timestamp per frame. <see cref="FrameCount"/> in the header is a placeholder until
/// <see cref="Close"/> patches in the real count, since it isn't known until recording stops.
/// </summary>
public sealed class SerWriter : ISerWriter
{
    private const string FileId = "LUCAM-RECORDER";
    private const int FrameCountOffset = 14 + 4 + 4 + 4 + 4 + 4 + 4; // past FileID/LuID/ColorID/LittleEndian/Width/Height/PixelDepth

    private FileStream? _stream;
    private BinaryWriter? _writer;
    private int _frameCount;
    private readonly List<long> _frameTimestampsUtcTicks = [];

    public void Open(string path, int width, int height, int bitDepth)
    {
        _frameCount = 0;
        _frameTimestampsUtcTicks.Clear();

        _stream = File.Create(path);
        _writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);

        WriteFixedAscii(FileId, 14);
        _writer.Write(0); // LuID - unused
        _writer.Write(0); // ColorID - mono only, see ICameraDevice's scope note
        // Per the SER format's long-standing real-world convention (not its literal spec wording):
        // 0 here still means the pixel bytes that follow are little-endian, matching what
        // FireCapture/SharpCap/PIPP/JSolex all actually write and expect on Windows.
        _writer.Write(0);
        _writer.Write(width);
        _writer.Write(height);
        _writer.Write(bitDepth);
        _writer.Write(0); // FrameCount - patched by Close()
        WriteFixedAscii(string.Empty, 40); // Observer
        WriteFixedAscii(string.Empty, 40); // Instrument
        WriteFixedAscii(string.Empty, 40); // Telescope
        _writer.Write(DateTime.Now.Ticks);
        _writer.Write(DateTime.UtcNow.Ticks);
    }

    public void WriteFrame(CameraFrame frame)
    {
        if (_writer is null)
        {
            throw new InvalidOperationException($"{nameof(SerWriter)}.{nameof(Open)} must be called before {nameof(WriteFrame)}.");
        }

        _writer.Write(frame.Data);
        _frameTimestampsUtcTicks.Add(frame.TimestampUtc.Ticks);
        _frameCount++;
    }

    public void Close()
    {
        if (_stream is null || _writer is null)
        {
            return;
        }

        foreach (var ticks in _frameTimestampsUtcTicks)
        {
            _writer.Write(ticks);
        }

        _writer.Flush();

        _stream.Position = FrameCountOffset;
        _stream.Write(BitConverter.GetBytes(_frameCount));

        _writer.Dispose();
        _stream.Dispose();
        _writer = null;
        _stream = null;
    }

    public void Dispose() => Close();

    private void WriteFixedAscii(string value, int length)
    {
        var bytes = new byte[length];
        var encoded = Encoding.ASCII.GetBytes(value);
        Array.Copy(encoded, bytes, Math.Min(encoded.Length, length));
        _writer!.Write(bytes);
    }
}
