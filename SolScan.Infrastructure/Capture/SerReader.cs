using System.Text;
using SolScan.Core.Camera;
using SolScan.Core.Capture;

namespace SolScan.Infrastructure.Capture;

/// <summary>
/// <see cref="ISerReader"/> reading the standard SER video format - the byte-for-byte inverse of
/// <see cref="SerWriter"/> (178-byte header, frame data back-to-back with no padding, then an optional
/// trailer of one 8-byte .NET-ticks timestamp per frame). Written to tolerate real-world files
/// <see cref="SerWriter"/> didn't produce - e.g. a Sunscan capture - which may declare a non-8/16
/// <c>PixelDepth</c> (14-bit ADC packed into a 2-byte container is common) or omit the timestamp
/// trailer entirely.
/// </summary>
public sealed class SerReader : ISerReader
{
    private const int HeaderSizeBytes = 178;

    private FileStream? _stream;
    private BinaryReader? _reader;
    private SerHeader? _header;
    private int _frameSizeBytes;

    public SerHeader Header => _header ?? throw new InvalidOperationException($"{nameof(SerReader)}.{nameof(Open)} must be called before {nameof(Header)} is read.");

    public void Open(string path)
    {
        Close();

        _stream = File.OpenRead(path);
        if (_stream.Length < HeaderSizeBytes)
        {
            var length = _stream.Length;
            Close();
            throw new InvalidDataException($"'{path}' is too short to be a valid .ser file (only {length} byte(s), expected at least {HeaderSizeBytes}).");
        }

        _reader = new BinaryReader(_stream, Encoding.ASCII, leaveOpen: true);

        var fileId = ReadFixedAscii(_reader, 14);
        _reader.ReadInt32(); // LuID - unused, matches SerWriter
        var colorId = _reader.ReadInt32();
        var littleEndianRaw = _reader.ReadInt32();
        var width = _reader.ReadInt32();
        var height = _reader.ReadInt32();
        var pixelDepth = _reader.ReadInt32();
        var frameCount = _reader.ReadInt32();
        var observer = ReadFixedAscii(_reader, 40);
        var instrument = ReadFixedAscii(_reader, 40);
        var telescope = ReadFixedAscii(_reader, 40);
        var dateTimeLocalTicks = _reader.ReadInt64();
        var dateTimeUtcTicks = _reader.ReadInt64();

        _header = new SerHeader(
            fileId,
            colorId,
            // Per SerWriter.Open's own comment: 0 here is the real-world convention for "little-endian",
            // not the literal spec wording.
            LittleEndian: littleEndianRaw == 0,
            width,
            height,
            pixelDepth,
            frameCount,
            observer,
            instrument,
            telescope,
            SafeDateTime(dateTimeLocalTicks),
            SafeDateTime(dateTimeUtcTicks));

        _frameSizeBytes = width * height * (pixelDepth <= 8 ? 1 : 2);
    }

    public CameraFrame ReadFrame(int index)
    {
        if (_stream is null || _reader is null || _header is null)
        {
            throw new InvalidOperationException($"{nameof(SerReader)}.{nameof(Open)} must be called before {nameof(ReadFrame)}.");
        }
        if (index < 0 || index >= _header.FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Must be between 0 and {_header.FrameCount - 1} for this file.");
        }

        var frameOffset = HeaderSizeBytes + (long)index * _frameSizeBytes;
        _stream.Position = frameOffset;
        var data = _reader.ReadBytes(_frameSizeBytes);

        var timestampUtc = TryReadFrameTimestampUtc(index) ?? DateTime.MinValue;

        return new CameraFrame(data, _header.Width, _header.Height, _header.PixelDepth, timestampUtc);
    }

    public void Close()
    {
        _reader?.Dispose();
        _stream?.Dispose();
        _reader = null;
        _stream = null;
        _header = null;
    }

    public void Dispose() => Close();

    /// <summary>Returns null (rather than throwing) when the file is too short to carry a trailer at
    /// all - real-world captures not written by <see cref="SerWriter"/> may not have one.</summary>
    private DateTime? TryReadFrameTimestampUtc(int index)
    {
        if (_stream is null || _reader is null || _header is null)
        {
            return null;
        }

        var trailerOffset = HeaderSizeBytes + (long)_header.FrameCount * _frameSizeBytes + (long)index * 8;
        if (trailerOffset + 8 > _stream.Length)
        {
            return null;
        }

        _stream.Position = trailerOffset;
        return SafeDateTime(_reader.ReadInt64());
    }

    private static DateTime SafeDateTime(long ticks) =>
        ticks >= 0 && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks) : DateTime.MinValue;

    private static string ReadFixedAscii(BinaryReader reader, int length) =>
        Encoding.ASCII.GetString(reader.ReadBytes(length)).TrimEnd('\0', ' ');
}
