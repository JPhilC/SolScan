using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SolScan.Core.Camera;

namespace SolScan.App.Services;

/// <summary>
/// Loads an arbitrary PNG/TIFF file (a screenshot, an exported still, anything) as a 16-bit mono
/// <see cref="CameraFrame"/> - used by <c>CaptureViewModel.LoadTestImage</c> to feed a static
/// image through the exact same live-preview pipeline (<c>ProcessPreviewFrame</c>: histogram,
/// contrast stretch, focus aid, spectral overlay) a real camera frame would go through, so the
/// spectral-line-labels/colour-band overlay can be tried and tuned without a camera or telescope
/// connected at all. Colour source images are converted to mono via WPF's own
/// <see cref="FormatConvertedBitmap"/> (standard luminance weighting) - same approach
/// <c>ProcessViewModel.LoadCameraFrameFromPng</c> already uses for its own (always-mono, always-PNG,
/// always previously-saved-by-SolScan-itself) preview loader; this one is deliberately more general,
/// since a test image can come from anywhere and in either format.
/// </summary>
public static class TestImageLoader
{
    public static CameraFrame Load(string path)
    {
        using var stream = File.OpenRead(path);
        BitmapDecoder decoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad),
            ".tif" or ".tiff" => new TiffBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad),
            var extension => throw new NotSupportedException($"Unsupported test image file type '{extension}' - only .png/.tif/.tiff are supported."),
        };

        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Gray16, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 2;
        var data = new byte[height * stride];
        converted.CopyPixels(data, stride, 0);

        return new CameraFrame(data, width, height, 16, DateTime.UtcNow);
    }
}
