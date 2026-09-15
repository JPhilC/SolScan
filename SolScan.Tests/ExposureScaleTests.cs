using SolScan.Core.Camera;

namespace SolScan.Tests;

public class ExposureScaleTests
{
    [Fact]
    public void Ranges_SpanTheWholeDomainWithNoGaps()
    {
        Assert.Equal(ExposureScale.MinMicroseconds, ExposureScale.Ranges[0].MinMicroseconds);
        Assert.Equal(ExposureScale.MaxMicroseconds, ExposureScale.Ranges[^1].MaxMicroseconds);

        for (var i = 1; i < ExposureScale.Ranges.Count; i++)
        {
            Assert.True(
                ExposureScale.Ranges[i].MinMicroseconds <= ExposureScale.Ranges[i - 1].MaxMicroseconds,
                $"Range {i} ({ExposureScale.Ranges[i].Label}) leaves a gap after range {i - 1} ({ExposureScale.Ranges[i - 1].Label}).");
        }
    }

    [Theory]
    [InlineData(32, "32µs ~ 10ms")]
    [InlineData(8_000, "32µs ~ 10ms")]
    [InlineData(10_000, "32µs ~ 10ms")] // shared boundary - the earlier (lower) range wins
    [InlineData(10_001, "1ms ~ 100ms")]
    [InlineData(500_000, "100ms ~ 1000ms")]
    [InlineData(5_000_000, "1s ~ 5s")]
    public void FindRange_PicksTheRangeContainingTheValue(double microseconds, string expectedLabel)
    {
        Assert.Equal(expectedLabel, ExposureScale.FindRange(microseconds).Label);
    }

    [Fact]
    public void FindRange_ClampsValuesOutsideTheWholeDomainToTheNearestEnd()
    {
        Assert.Equal(ExposureScale.Ranges[0], ExposureScale.FindRange(0));
        Assert.Equal(ExposureScale.Ranges[^1], ExposureScale.FindRange(10_000_000));
    }

    [Fact]
    public void OnlyTheLastRangeAllowsFractionalValues()
    {
        for (var i = 0; i < ExposureScale.Ranges.Count - 1; i++)
        {
            Assert.Equal(0, ExposureScale.Ranges[i].DecimalPlaces);
        }

        Assert.True(ExposureScale.Ranges[^1].DecimalPlaces > 0);
    }
}
