using SolScan.Core.Camera;
using SolScan.Processing.Spectrum;

namespace SolScan.Tests;

public class LiveCurvatureFitterTests
{
    [Theory]
    [InlineData(3840)] // stride 4 - the decimated path used on a real full-width sensor
    [InlineData(300)]  // stride 1 - identical to the plain detector
    public void Fit_RecoversTheTrueLineRowAcrossTheFullWidth(int width)
    {
        const int height = 300;
        const double sigma = 3;
        const double baseRow = 150;
        // ~6px of "smile" drift from the middle column out to either edge, whatever the width.
        var curvature = 6.0 / System.Math.Pow(width / 2.0, 2);

        var data = new byte[width * height * 2];
        for (var x = 0; x < width; x++)
        {
            var dx = x - (width / 2.0);
            var lineRow = baseRow + (curvature * dx * dx);
            for (var y = 0; y < height; y++)
            {
                var offset = y - lineRow;
                var value = (ushort)(20000 - (8000 * System.Math.Exp(-(offset * offset) / (2 * sigma * sigma))));
                var index = ((y * width) + x) * 2;
                data[index] = (byte)value;
                data[index + 1] = (byte)(value >> 8);
            }
        }

        var fit = LiveCurvatureFitter.Fit(new CameraFrame(data, width, height, 16, DateTime.UtcNow));

        // The fit must be expressed in *full-frame* column coordinates even when it was made on a
        // column-decimated copy - checked at both edges and the middle.
        foreach (var x in new[] { 0, width / 2, width - 1 })
        {
            var dx = x - (width / 2.0);
            Assert.InRange(fit.Evaluate(x), baseRow + (curvature * dx * dx) - 0.5, baseRow + (curvature * dx * dx) + 0.5);
        }
    }
}
