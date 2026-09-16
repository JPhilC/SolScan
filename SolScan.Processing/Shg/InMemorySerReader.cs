using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SolScan.Core.Camera;
using SolScan.Core.Capture;

namespace SolScan.Processing.Shg;

/// <summary>
/// An <see cref="ISerReader"/> backed by an already-populated in-memory copy of every frame's raw
/// pixel bytes, rather than a file handle - lets <see cref="ShgProcessor"/> read a recording's frames
/// from disk exactly once (via <see cref="LoadFrom"/>) and then hand this same instance to both
/// <see cref="FrameAverager"/>'s two passes and <see cref="DiskReconstructor"/>'s own reconstruction
/// pass, instead of each independently re-reading the file from disk. See
/// <see cref="ShgProcessor"/>'s own doc comment and CLAUDE.md's "closing the I/O throughput gap"
/// investigation for why this matters and when it's actually used (only when the recording comfortably
/// fits in available memory - a large legacy capture still falls back to today's disk-reading path).
///
/// Neither <see cref="FrameAverager"/> nor <see cref="DiskReconstructor"/> needed any changes to
/// support this - they already accept a plain <see cref="ISerReader"/>, and this type satisfies that
/// same contract. Every <see cref="ReadFrame"/> call only ever reads its own already-populated array
/// slot, and nothing ever mutates after construction, so - unlike the real disk-backed
/// <c>SolScan.Infrastructure.Capture.SerReader</c>, which needed a deliberate rewrite to become
/// thread-safe - this type is trivially safe for concurrent use from the very same <c>Parallel.For</c>
/// loops those two classes already run.
/// </summary>
public sealed class InMemorySerReader : ISerReader
{
    private readonly SerHeader _header;
    private readonly byte[][] _frames;

    private InMemorySerReader(SerHeader header, byte[][] frames)
    {
        _header = header;
        _frames = frames;
    }

    public SerHeader Header => _header;

    /// <summary>Does the one real disk pass this whole type exists to make unnecessary a second time -
    /// reads every frame's raw bytes from <paramref name="realReader"/> (already open) into memory, in
    /// parallel across CPU cores (safe because the real disk-backed reader's own <c>ReadFrame</c> is
    /// genuinely thread-safe - see its own doc comment), and reports real throughput the same way
    /// <see cref="FrameAverager"/>/<see cref="DiskReconstructor"/> already do.</summary>
    public static InMemorySerReader LoadFrom(ISerReader realReader, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var header = realReader.Header;
        var frameCount = header.FrameCount;
        var frames = new byte[frameCount][];
        var stopwatch = Stopwatch.StartNew();

        Parallel.For(0, frameCount, new ParallelOptions { CancellationToken = cancellationToken }, i =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Timestamps are never needed here - this type never carries them either (see ReadFrame's
            // own doc comment) - so skip the trailer read the same way the hot averaging/reconstruction
            // loops already do.
            frames[i] = realReader.ReadFrame(i, includeTimestamp: false).Data;
        });

        var frameSizeBytes = (long)header.Width * header.Height * (header.PixelDepth <= 8 ? 1 : 2);
        progress?.Report($"Loaded into memory: {FrameConversion.FormatThroughput(frameSizeBytes * frameCount, stopwatch.Elapsed)}");

        return new InMemorySerReader(header, frames);
    }

    /// <summary>Always throws - this type is constructed pre-populated via <see cref="LoadFrom"/>,
    /// never opened by path.</summary>
    public void Open(string path) =>
        throw new NotSupportedException($"{nameof(InMemorySerReader)} is constructed pre-populated via {nameof(LoadFrom)} - {nameof(Open)} is not supported.");

    /// <summary><paramref name="includeTimestamp"/> is accepted for interface compatibility but has no
    /// effect - this type never carries real per-frame timestamps at all (always returns
    /// <see cref="DateTime.MinValue"/>), since its only intended callers already pass <c>false</c>
    /// anyway. A deliberate simplification, not an oversight.</summary>
    public CameraFrame ReadFrame(int index, bool includeTimestamp = true) =>
        new(_frames[index], _header.Width, _header.Height, _header.PixelDepth, DateTime.MinValue);

    /// <summary>Nothing to release - the frame buffers are plain managed memory, reclaimed by the GC
    /// like any other object once nothing references this instance any longer.</summary>
    public void Close()
    {
    }

    public void Dispose() => Close();
}
