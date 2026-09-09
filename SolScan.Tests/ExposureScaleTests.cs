using SolScan.Core.Camera;

namespace SolScan.Tests;

public class ExposureScaleTests
{
    [Fact]
    public void ToSliderPosition_MapsRangeEndsToZeroAndOne()
    {
        Assert.Equal(0, ExposureScale.ToSliderPosition(ExposureScale.MinMicroseconds));
        Assert.Equal(1, ExposureScale.ToSliderPosition(ExposureScale.MaxMicroseconds));
    }

    [Fact]
    public void FromSliderPosition_MapsZeroAndOneToRangeEnds()
    {
        // Relative tolerance rather than xUnit's decimal-place precision - Math.Exp(Math.Log(x))
        // round-trips to within a tiny relative error, which is a much larger *absolute* gap at
        // MaxMicroseconds' scale (millions of microseconds) than at MinMicroseconds' (tens).
        AssertClose(ExposureScale.MinMicroseconds, ExposureScale.FromSliderPosition(0));
        AssertClose(ExposureScale.MaxMicroseconds, ExposureScale.FromSliderPosition(1));
    }

    [Fact]
    public void SliderPosition_RoundTripsThroughMidRange()
    {
        const double exposureMicroseconds = 10_000;

        var position = ExposureScale.ToSliderPosition(exposureMicroseconds);
        var roundTripped = ExposureScale.FromSliderPosition(position);

        Assert.InRange(position, 0, 1);
        AssertClose(exposureMicroseconds, roundTripped);
    }

    [Theory]
    [InlineData(500, "500 µs")]
    [InlineData(1_500, "1.5 ms")]
    [InlineData(2_500_000, "2.500 s")]
    public void Format_PicksTheMostReadableUnit(double microseconds, string expected)
    {
        Assert.Equal(expected, ExposureScale.Format(microseconds));
    }

    private static void AssertClose(double expected, double actual, double relativeTolerance = 1e-6) =>
        Assert.True(
            Math.Abs(actual - expected) <= Math.Abs(expected) * relativeTolerance,
            $"Expected {actual} to be within {relativeTolerance:P} of {expected}.");
}
