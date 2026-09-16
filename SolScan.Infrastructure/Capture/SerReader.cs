using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
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
///
/// Reads via <see cref="RandomAccess"/> against one shared <see cref="SafeFileHandle"/> rather than a
/// <see cref="FileStream"/>/<see cref="BinaryReader"/> pair with a shared, mutable position field -
/// every <see cref="RandomAccess.Read(SafeFileHandle,Span{byte},long)"/> call carries its own explicit
/// file offset, so <see cref="ReadFrame"/> is genuinely safe to call concurrently from multiple threads
/// on the same open instance (no shared state a concurrent call could race on). This is what lets
/// <see cref="SolScan.Processing.Shg.FrameAverager"/>/<see cref="SolScan.Processing.Shg.DiskReconstructor"/>
/// parallelize their per-frame work across CPU cores without needing a separate reader instance per
/// worker thread - see CLAUDE.md's "speeding up processing" investigation for the full reasoning.
/// </summary>
public sealed class SerReader : ISerReader
{
    private const int HeaderSizeBytes = 178;

    private SafeFileHandle? _handle;
    private SerHeader? _header;
    private int _frameSizeBytes;

    // Cached once at Open time - the file is only ever read from here, so its length can't change for
    // the lifetime of an open reader. Removes a RandomAccess.GetLength syscall from every single
    // TryReadFrameTimestampUtc call (the original FileStream-based version queried _stream.Length the
    // same way per call - this is a small additional improvement, not a behaviour change).
    private long _fileLength;

    public SerHeader Header => _header ?? throw new InvalidOperationException($"{nameof(SerReader)}.{nameof(Open)} must be called before {nameof(Header)} is read.");

    public void Open(string path)
    {
        Close();

        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        var length = RandomAccess.GetLength(handle);
        if (length < HeaderSizeBytes)
        {
            handle.Dispose();
            throw new InvalidDataException($"'{path}' is too short to be a valid .ser file (only {length} byte(s), expected at least {HeaderSizeBytes}).");
        }

        _handle = handle;
        _fileLength = length;

        var headerBytes = new byte[HeaderSizeBytes];
        ReadExactly(handle, headerBytes, 0);
        var header = (ReadOnlySpan<byte>)headerBytes;

        var fileId = ReadFixedAscii(header[..14]);
        // header[14..18] is LuID - unused, matches SerWriter.
        var colorId = BinaryPrimitives.ReadInt32LittleEndian(header[18..22]);
        var littleEndianRaw = BinaryPrimitives.ReadInt32LittleEndian(header[22..26]);
        var width = BinaryPrimitives.ReadInt32LittleEndian(header[26..30]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(header[30..34]);
        var pixelDepth = BinaryPrimitives.ReadInt32LittleEndian(header[34..38]);
        var frameCount = BinaryPrimitives.ReadInt32LittleEndian(header[38..42]);
        var observer = ReadFixedAscii(header[42..82]);
        var instrument = ReadFixedAscii(header[82..122]);
        var telescope = ReadFixedAscii(header[122..162]);
        var dateTimeLocalTicks = BinaryPrimitives.ReadInt64LittleEndian(header[162..170]);
        var dateTimeUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(header[170..178]);

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

    /// <remarks>Thread-safe: see this class's own doc comment.</remarks>
    public CameraFrame ReadFrame(int index, bool includeTimestamp = true)
    {
        if (_handle is null || _header is null)
        {
            throw new InvalidOperationException($"{nameof(SerReader)}.{nameof(Open)} must be called before {nameof(ReadFrame)}.");
        }
        if (index < 0 || index >= _header.FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Must be between 0 and {_header.FrameCount - 1} for this file.");
        }

        var frameOffset = HeaderSizeBytes + (long)index * _frameSizeBytes;
        var data = new byte[_frameSizeBytes];
        ReadExactly(_handle, data, frameOffset);

        // Skipping this when the caller doesn't need it avoids seeking all the way to the trailer
        // region (past every frame's pixel data) and back on every single call - see this parameter's
        // own doc comment on ISerReader.ReadFrame.
        var timestampUtc = includeTimestamp ? TryReadFrameTimestampUtc(index) ?? DateTime.MinValue : DateTime.MinValue;

        return new CameraFrame(data, _header.Width, _header.Height, _header.PixelDepth, timestampUtc);
    }

    public void Close()
    {
        _handle?.Dispose();
        _handle = null;
        _header = null;
    }

    public void Dispose() => Close();

    /// <summary>Returns null (rather than throwing) when the file is too short to carry a trailer at
    /// all - real-world captures not written by <see cref="SerWriter"/> may not have one.</summary>
    private DateTime? TryReadFrameTimestampUtc(int index)
    {
        if (_handle is null || _header is null)
        {
            return null;
        }

        var trailerOffset = HeaderSizeBytes + (long)_header.FrameCount * _frameSizeBytes + (long)index * 8;
        if (trailerOffset + 8 > _fileLength)
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[8];
        ReadExactly(_handle, buffer, trailerOffset);
        return SafeDateTime(BinaryPrimitives.ReadInt64LittleEndian(buffer));
    }

    /// <summary>Loops until <paramref name="buffer"/> is completely filled from <paramref name="offset"/> -
    /// <see cref="RandomAccess.Read(SafeFileHandle,Span{byte},long)"/> is permitted to return fewer
    /// bytes than requested (standard positional-read semantics), even though a single call reading
    /// well within a local file's own bounds essentially always returns the full amount in practice.</summary>
    private static void ReadExactly(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = RandomAccess.Read(handle, buffer[totalRead..], offset + totalRead);
            if (read == 0)
            {
                throw new EndOfStreamException($"Unexpected end of file at offset {offset + totalRead}.");
            }

            totalRead += read;
        }
    }

    private static DateTime SafeDateTime(long ticks) =>
        ticks >= 0 && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks) : DateTime.MinValue;

    private static string ReadFixedAscii(ReadOnlySpan<byte> bytes) =>
        Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');
}
