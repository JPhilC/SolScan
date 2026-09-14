using System.Threading;
using SolScan.Core.Capture;

namespace SolScan.Processing.Capture;

/// <summary>
/// <see cref="ISerCropper"/> reading every frame of the source file via <see cref="ISerReader"/> and
/// re-writing a centred vertical slice of it via <see cref="ISerWriter"/> - same "resolve the concrete
/// reader/writer through a DI factory delegate, never reference SolScan.Infrastructure directly" shape
/// as <see cref="Shg.ShgProcessor"/>'s own constructor, so this stays testable against
/// SolScan.Simulators/hand-built fakes without any real file IO in unit tests.
/// </summary>
public sealed class SerCropper : ISerCropper
{
    private readonly Func<ISerReader> _serReaderFactory;
    private readonly Func<ISerWriter> _serWriterFactory;

    public SerCropper(Func<ISerReader> serReaderFactory, Func<ISerWriter> serWriterFactory)
    {
        _serReaderFactory = serReaderFactory;
        _serWriterFactory = serWriterFactory;
    }

    public Task<SerCropResult> CropAsync(
        string sourcePath,
        string outputPath,
        double heightFraction,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Crop(sourcePath, outputPath, heightFraction, progress, cancellationToken), cancellationToken);

    private SerCropResult Crop(string sourcePath, string outputPath, double heightFraction, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (heightFraction <= 0 || heightFraction > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(heightFraction), heightFraction, "Must be greater than 0 and at most 1.");
        }

        using var reader = _serReaderFactory();
        reader.Open(sourcePath);
        var header = reader.Header;
        if (header.FrameCount == 0)
        {
            throw new InvalidOperationException("The SER file has no frames.");
        }

        var croppedHeight = ComputeCroppedHeight(header.Height, heightFraction);
        var rowOffset = (header.Height - croppedHeight) / 2;
        var bytesPerPixel = header.PixelDepth <= 8 ? 1 : 2;
        var rowStrideBytes = header.Width * bytesPerPixel;
        var croppedFrameSizeBytes = croppedHeight * rowStrideBytes;

        using var writer = _serWriterFactory();
        writer.Open(outputPath, header.Width, croppedHeight, header.PixelDepth);

        try
        {
            progress?.Report($"Cropping {header.FrameCount} frame(s) from {header.Width}x{header.Height} to {header.Width}x{croppedHeight}...");
            for (var frameIndex = 0; frameIndex < header.FrameCount; frameIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var frame = reader.ReadFrame(frameIndex);
                var croppedData = new byte[croppedFrameSizeBytes];
                Buffer.BlockCopy(frame.Data, rowOffset * rowStrideBytes, croppedData, 0, croppedFrameSizeBytes);

                writer.WriteFrame(frame with { Data = croppedData, Height = croppedHeight });

                if (frameIndex % 100 == 0)
                {
                    progress?.Report($"Cropping... {frameIndex}/{header.FrameCount}");
                }
            }
        }
        finally
        {
            // Close (not just Dispose) even on cancellation/failure, so the file's FrameCount header
            // field is patched to whatever was actually written rather than left at SerWriter.Open's
            // own placeholder zero - a partial file this leaves behind should still describe itself
            // correctly, even though the caller will typically delete it on cancellation anyway.
            writer.Close();
        }

        return new SerCropResult(header.FrameCount, header.Width, header.Height, croppedHeight, rowOffset);
    }

    /// <summary>Resolves <paramref name="heightFraction"/> (0, 1] against <paramref name="sourceHeight"/>,
    /// rounding to the nearest row and clamping to at least 1 - shared with <c>SerCropViewModel</c>'s
    /// own live preview of the resulting height, so the number shown before cropping starts always
    /// matches what actually gets written.</summary>
    public static int ComputeCroppedHeight(int sourceHeight, double heightFraction) =>
        System.Math.Clamp((int)System.Math.Round(sourceHeight * heightFraction, MidpointRounding.AwayFromZero), 1, sourceHeight);
}
