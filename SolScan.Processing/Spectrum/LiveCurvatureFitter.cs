using SolScan.Core.Camera;
using SolScan.Processing.Math;
using SolScan.Processing.Shg;

namespace SolScan.Processing.Spectrum;

/// <summary>
/// A cheaper line-curvature fit for *live* frames. <see cref="SpectralLineCurvatureDetector.Detect"/>
/// walks every column of the frame it's given (allocating per column, striding down a 2D array), which
/// measured ~790ms on a full 3840x2160 frame in a Release build - fine for the offline pipeline's one
/// averaged frame, far too slow to repeat several times a second in a live preview. The "smile" it fits
/// is a smooth 2nd-order curve, so a fit on every Nth column loses nothing meaningful: this builds a
/// column-decimated copy (keeping every row - the vertical, dispersion-axis resolution is what the
/// sub-pixel line centre depends on), runs the same detector on it, and rescales the resulting
/// polynomial back into full-frame column coordinates. Cost scales with the column count, so a stride
/// of 4 is ~4x cheaper (converting only the sampled columns from bytes helps too).
///
/// Frames narrower than <see cref="TargetColumns"/> get a stride of 1, i.e. exactly the same fit
/// <see cref="SpectralLineCurvatureDetector.Detect"/> would have produced.
/// </summary>
public static class LiveCurvatureFitter
{
    /// <summary>Roughly how many columns the fit samples - the stride is chosen to land near this.</summary>
    private const int TargetColumns = 960;

    public static QuadraticPolynomial Fit(CameraFrame frame)
    {
        var stride = System.Math.Max(1, frame.Width / TargetColumns);
        var sampledColumns = (frame.Width + stride - 1) / stride;
        var bytesPerPixel = frame.BitDepth > 8 ? 2 : 1;
        var rowStride = frame.Width * bytesPerPixel;
        var data = frame.Data;

        var decimated = new float[frame.Height, sampledColumns];
        for (var y = 0; y < frame.Height; y++)
        {
            var rowOffset = y * rowStride;
            for (var i = 0; i < sampledColumns; i++)
            {
                var index = rowOffset + (i * stride * bytesPerPixel);
                decimated[y, i] = bytesPerPixel == 2 ? data[index] | (data[index + 1] << 8) : data[index];
            }
        }

        var fit = new SpectralLineCurvatureDetector().Detect(decimated);
        if (stride == 1)
        {
            return fit;
        }

        // y = a'*x'^2 + b'*x' + c' with x' = x / stride, rows unchanged.
        return new QuadraticPolynomial(fit.A / (stride * stride), fit.B / stride, fit.C);
    }
}
