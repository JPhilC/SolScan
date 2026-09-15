using SolScan.Core.Camera;
using SolScan.Core.Equipment;
using SolScan.Core.Processing;
using SolScan.Processing.Spectrum;

namespace SolScan.Tests;

public class SpectralOverlayAnalyzerTests
{
    private static readonly SpectrographProfile Instrument = SpectrographProfile.CreateSolEx();
    private const double PixelSizeMicrons = 2.0;

    /// <summary>Same asymmetric two-dip shape idea as <c>SpectralLineIdentifierTests</c> - a plain
    /// symmetric Gaussian correlates too well against another Gaussian of a different width to be a
    /// meaningful discrimination test (see that class's own doc comment for the real finding behind
    /// this).</summary>
    private static double HAlphaLikeShape(double offsetAngstroms) =>
        9000
        - (3000 * System.Math.Exp(-(offsetAngstroms * offsetAngstroms) / (2 * 0.3 * 0.3)))
        - (600 * System.Math.Exp(-System.Math.Pow(offsetAngstroms - 1.2, 2) / (2 * 0.15 * 0.15)));

    [Fact]
    public void Analyze_SingleFrameCenteredOnHAlpha_IdentifiesHAlphaAndPlacesNeighbours()
    {
        const int width = 200;
        const int height = 400;
        const double trueRow = 200;

        var dispersion = SpectralDispersion.ComputeAngstromsPerPixel(Instrument, SpectralRay.HAlpha.WavelengthAngstroms, PixelSizeMicrons);
        var frame = BuildSyntheticFrame(width, height, trueRow, dispersion, HAlphaLikeShape);

        var hAlphaWindow = BuildSyntheticWindow(SpectralRay.HAlpha.WavelengthAngstroms, HAlphaLikeShape);
        // A second, distinctly-shaped candidate so the identifier has more than one thing to compare
        // against - matching SpectralLineIdentifierTests' own setup.
        var calciumKWindow = BuildSyntheticWindow(SpectralRay.CalciumK.WavelengthAngstroms, offset =>
            9000 - (1000 * System.Math.Exp(-(offset * offset) / (2 * 0.6 * 0.6))) - (900 * System.Math.Exp(-System.Math.Pow(offset + 0.8, 2) / (2 * 0.3 * 0.3))));

        var result = SpectralOverlayAnalyzer.Analyze(frame, Instrument, PixelSizeMicrons, binning: 1, maxShiftPixels: 150,
            referenceWindows: [hAlphaWindow, calciumKWindow]);

        Assert.Equal(SpectralRay.HAlpha, result.Identification.IdentifiedRay);
        Assert.NotNull(result.AnchorDispersionAngstromsPerPixel);
        Assert.InRange(result.CentreRowInFrame, trueRow - 1, trueRow + 1); // the fitted curve should recover the true row

        var hAlphaEntry = Assert.Single(result.VisibleLines, l => l.Ray == SpectralRay.HAlpha);
        Assert.InRange(hAlphaEntry.PixelShiftFromCentre, -1, 1); // the anchor itself sits at ~shift 0
        Assert.InRange(hAlphaEntry.RowInFrame, trueRow - 1, trueRow + 1); // RowInFrame = CentreRowInFrame + shift, shift ~0

        // Ca K's own catalog wavelength is ~2629Å away from H-alpha - projected using H-alpha's own
        // dispersion, that's far outside a 150px window, so it should NOT appear as "visible" here.
        Assert.DoesNotContain(result.VisibleLines, l => l.Ray == SpectralRay.CalciumK);
    }

    [Fact]
    public void Analyze_NoReferenceWindows_ReturnsEmptyResultRatherThanThrowing()
    {
        var frame = BuildSyntheticFrame(50, 100, 50, dispersionAngstromsPerPixel: 0.05, HAlphaLikeShape);

        var result = SpectralOverlayAnalyzer.Analyze(frame, Instrument, PixelSizeMicrons, referenceWindows: []);

        Assert.Null(result.Identification.IdentifiedRay);
        Assert.Empty(result.VisibleLines);
        Assert.Null(result.AnchorDispersionAngstromsPerPixel);
    }

    private static ReferenceWindow BuildSyntheticWindow(double centerWavelengthAngstroms, Func<double, double> shape)
    {
        const double halfWidth = 8.0;
        const double step = 0.01;
        var count = (int)System.Math.Round((2 * halfWidth / step) + 1);
        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = shape(-halfWidth + (i * step));
        }

        return new ReferenceWindow(centerWavelengthAngstroms, step, values);
    }

    /// <summary>A single 16-bit synthetic frame whose row profile (constant across every column, no
    /// curvature) follows <paramref name="shape"/> around <paramref name="trueRow"/>, at the given
    /// dispersion - packed into a real <see cref="CameraFrame"/>'s little-endian byte layout, so this
    /// exercises <see cref="SolScan.Processing.Shg.FrameConversion.ToFloatArray"/> too, not just the
    /// already-tested pieces downstream of it.</summary>
    private static CameraFrame BuildSyntheticFrame(int width, int height, double trueRow, double dispersionAngstromsPerPixel, Func<double, double> shape)
    {
        var data = new byte[width * height * 2];
        for (var y = 0; y < height; y++)
        {
            var offsetAngstroms = (y - trueRow) * dispersionAngstromsPerPixel;
            var value = (ushort)System.Math.Clamp(shape(offsetAngstroms), 0, ushort.MaxValue);
            for (var x = 0; x < width; x++)
            {
                var index = ((y * width) + x) * 2;
                data[index] = (byte)value;
                data[index + 1] = (byte)(value >> 8);
            }
        }

        return new CameraFrame(data, width, height, 16, DateTime.UtcNow);
    }
}
