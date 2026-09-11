using SolScan.Core.Camera;

namespace SolScan.Processing.Shg;

/// <summary>Shared pixel-decoding helper for <see cref="FrameAverager"/>/<see cref="DiskReconstructor"/> -
/// converts a raw <see cref="CameraFrame"/> into a native-scale <c>float[,]</c> (row-major,
/// <c>[y, x]</c>, matching astro4j's own <c>float[][]</c> convention where rows are the
/// spectral/dispersion axis and columns are the spatial axis). Same bytes-per-pixel/little-endian
/// decoding convention <see cref="SolScan.Core.Camera.FramePreview"/> and
/// <c>SolScan.Infrastructure.Capture.SerReader</c> already establish - not reinvented here.</summary>
internal static class FrameConversion
{
    public static float[,] ToFloatArray(CameraFrame frame)
    {
        var bytesPerPixel = frame.BitDepth > 8 ? 2 : 1;
        var rowStride = frame.Width * bytesPerPixel;
        var data = frame.Data;
        var result = new float[frame.Height, frame.Width];

        for (var y = 0; y < frame.Height; y++)
        {
            var rowOffset = y * rowStride;
            for (var x = 0; x < frame.Width; x++)
            {
                var index = rowOffset + (x * bytesPerPixel);
                var value = bytesPerPixel == 2 ? data[index] | (data[index + 1] << 8) : data[index];
                result[y, x] = value;
            }
        }

        return result;
    }

    public static double MeanOf(float[,] frame)
    {
        double sum = 0;
        var height = frame.GetLength(0);
        var width = frame.GetLength(1);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                sum += frame[y, x];
            }
        }

        return sum / ((double)height * width);
    }
}
