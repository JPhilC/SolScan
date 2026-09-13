using SolScan.Core.Processing;
using SolScan.Processing.Color;

namespace SolScan.Tests;

public class ColorizeTests
{
    [Fact]
    public void ColorCurve_PassesThroughItsThreeAnchorPointsExactly()
    {
        // A 3-point quadratic fit should have zero residual at all three of its own anchor points -
        // (0,0), (in<<8, out<<8) and (65535,65535) - for every channel.
        var curveParams = new ColorCurveParams(RIn: 84, ROut: 139, GIn: 95, GOut: 20, BIn: 218, BOut: 65);
        var curve = new ColorCurve(curveParams);

        var mono = new float[1, 3];
        mono[0, 0] = 0;
        mono[0, 1] = 84 << 8;
        mono[0, 2] = 65535;

        var (r, g, b) = curve.Apply(mono);

        Assert.Equal(0, r[0, 0]);
        Assert.Equal(0, g[0, 0]);
        Assert.Equal(0, b[0, 0]);

        Assert.Equal(139 << 8, r[0, 1], 1);

        Assert.Equal(65535, r[0, 2], 1);
        Assert.Equal(65535, g[0, 2], 1);
        Assert.Equal(65535, b[0, 2], 1);
    }

    [Fact]
    public void ColorCurve_OutputStaysWithinRange()
    {
        var curve = new ColorCurve(ColorCurveParams.HAlpha);
        var mono = new float[1, 5];
        for (var x = 0; x < 5; x++)
        {
            mono[0, x] = x * 65535f / 4;
        }

        var (r, g, b) = curve.Apply(mono);
        for (var x = 0; x < 5; x++)
        {
            Assert.InRange(r[0, x], 0, 65535);
            Assert.InRange(g[0, x], 0, 65535);
            Assert.InRange(b[0, x], 0, 65535);
        }
    }

    [Theory]
    [InlineData(3933.66, true, false)] // Calcium K - violet/blue: expect blue to dominate over red
    [InlineData(5889.95, false, true)] // Sodium D2 - yellow: expect red and green both present, blue near zero
    public void ToRgb_ApproximatesTheExpectedHueForKnownWavelengths(double wavelengthAngstroms, bool expectBlueDominant, bool expectYellow)
    {
        var ray = new SpectralRay("test", wavelengthAngstroms, false);
        var (r, g, b) = ray.ToRgb();

        if (expectBlueDominant)
        {
            Assert.True(b > r, $"Expected blue ({b}) to dominate red ({r}) for a violet/blue wavelength.");
        }

        if (expectYellow)
        {
            Assert.True(b < r && b < g, $"Expected blue ({b}) to be the weakest channel for a yellow wavelength (r={r}, g={g}).");
        }
    }

    [Fact]
    public void ToRgb_OtherRayWithZeroWavelength_ReturnsNeutralGray()
    {
        // Meaningless input (SpectralRay.Other's own wavelength) - callers are expected to check
        // WavelengthAngstroms > 0 first (see SpectralRay.Other's own doc comment). The underlying
        // wavelength-to-colour mapping has no visible-spectrum bucket for 0nm, so ToSimpleRgb comes
        // back pure black - but ImproveEsthetics' own "lighten by 45% of the remaining headroom to
        // white" step still applies uniformly regardless of input, so the actual result is a neutral
        // (saturation-0) mid-gray, not literal black. Shouldn't throw either way.
        var (r, g, b) = SpectralRay.Other.ToRgb();
        Assert.Equal(r, g);
        Assert.Equal(g, b);
        Assert.True(r > 0, "Expected ImproveEsthetics' lightening step to move black off pure 0.");
    }

    [Fact]
    public void WithCurve_ProducesCorrectlyShapedChannelsForHAlpha()
    {
        var mono = new float[4, 4];
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                mono[y, x] = ((y * 4) + x) * 65535f / 15;
            }
        }

        var (r, g, b) = Colorize.WithCurve(mono, ColorCurveParams.HAlpha);

        Assert.Equal(4, r.GetLength(0));
        Assert.Equal(4, r.GetLength(1));

        // H-alpha's curve (rOut=139 > rIn=84, gOut=20 < gIn=95, bOut=65 < bIn=218) lifts red relative
        // to green at the curve's own anchor input (84 << 8) - the reddish tint the "colorized
        // H-alpha" image is actually meant to have.
        var curve = new ColorCurve(ColorCurveParams.HAlpha);
        var (anchorR, anchorG, _) = curve.Apply(new float[,] { { 84 << 8 } });
        Assert.True(anchorR[0, 0] > anchorG[0, 0], "Expected H-alpha's curve to favour red over green at its own anchor input.");
    }

    [Fact]
    public void WithWavelengthRgb_TintsProportionallyToTheGivenColour()
    {
        var mono = new float[3, 3];
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                mono[y, x] = 30000f;
            }
        }

        // A pure-red tint should leave green/blue at (or very near) zero and red meaningfully above
        // zero, once gamma-stretched and lightness-stretched.
        var (r, g, b) = Colorize.WithWavelengthRgb(mono, (255, 0, 0));

        var anyRedAboveZero = false;
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                Assert.Equal(0, g[y, x]);
                Assert.Equal(0, b[y, x]);
                if (r[y, x] > 0)
                {
                    anyRedAboveZero = true;
                }
            }
        }

        Assert.True(anyRedAboveZero, "Expected a pure-red tint to leave at least some red signal in the output.");
    }
}
