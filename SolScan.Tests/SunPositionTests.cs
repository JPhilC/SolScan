using SolScan.Core.Astronomy;

namespace SolScan.Tests;

public class SunPositionTests
{
    // Loose tolerances - this is a sanity check that the algorithm lands in the right neighbourhood
    // at well-known moments, not a precision validation of the low-precision formulas themselves
    // (documented accuracy ~0.01°, i.e. far tighter than these).
    private const double DecToleranceDeg = 1.0;
    private const double RaToleranceHours = 0.2;

    [Fact]
    public void GetApparentRaDecJNow_AtJuneSolstice_DeclinationNearMaxNorth()
    {
        // 2024-06-20 20:51 UTC - the actual June solstice instant.
        var (raHours, decDeg) = SunPosition.GetApparentRaDecJNow(new DateTime(2024, 6, 20, 20, 51, 0, DateTimeKind.Utc));

        Assert.InRange(decDeg, 23.4 - DecToleranceDeg, 23.4 + DecToleranceDeg);
        Assert.InRange(raHours, NormalizeHours(6 - RaToleranceHours), NormalizeHours(6 + RaToleranceHours));
    }

    [Fact]
    public void GetApparentRaDecJNow_AtDecemberSolstice_DeclinationNearMaxSouth()
    {
        // 2024-12-21 09:20 UTC - the actual December solstice instant.
        var (raHours, decDeg) = SunPosition.GetApparentRaDecJNow(new DateTime(2024, 12, 21, 9, 20, 0, DateTimeKind.Utc));

        Assert.InRange(decDeg, -23.4 - DecToleranceDeg, -23.4 + DecToleranceDeg);
        Assert.InRange(raHours, 18 - RaToleranceHours, 18 + RaToleranceHours);
    }

    [Fact]
    public void GetApparentRaDecJNow_AtMarchEquinox_DeclinationNearZero()
    {
        // 2024-03-20 03:06 UTC - the actual March equinox instant.
        var (raHours, decDeg) = SunPosition.GetApparentRaDecJNow(new DateTime(2024, 3, 20, 3, 6, 0, DateTimeKind.Utc));

        Assert.InRange(decDeg, -DecToleranceDeg, DecToleranceDeg);
        // RA wraps around 0h/24h here, so compare distance to 0 the wraparound-safe way rather than
        // a plain InRange (which can't express a range straddling the 24h -> 0h wrap).
        Assert.InRange(Math.Min(raHours, 24 - raHours), 0, RaToleranceHours);
    }

    [Fact]
    public void GetApparentRaDecJNow_AtSeptemberEquinox_DeclinationNearZero()
    {
        // 2024-09-22 12:44 UTC - the actual September equinox instant.
        var (raHours, decDeg) = SunPosition.GetApparentRaDecJNow(new DateTime(2024, 9, 22, 12, 44, 0, DateTimeKind.Utc));

        Assert.InRange(decDeg, -DecToleranceDeg, DecToleranceDeg);
        Assert.InRange(raHours, 12 - RaToleranceHours, 12 + RaToleranceHours);
    }

    [Fact]
    public void GetApparentRaDecJNow_ConvertsLocalTimeToUtc()
    {
        var utc = new DateTime(2024, 6, 20, 20, 51, 0, DateTimeKind.Utc);
        var local = utc.ToLocalTime(); // same instant, just Kind == Local

        var fromUtc = SunPosition.GetApparentRaDecJNow(utc);
        var fromLocal = SunPosition.GetApparentRaDecJNow(local);

        // Both describe the same instant, so the two results should agree almost exactly.
        Assert.InRange(Math.Abs(fromUtc.RaHours - fromLocal.RaHours), 0, 0.001);
        Assert.InRange(Math.Abs(fromUtc.DecDeg - fromLocal.DecDeg), 0, 0.001);
    }

    [Fact]
    public void GetApparentRaDecJNow_ReturnsValuesInValidRanges()
    {
        var (raHours, decDeg) = SunPosition.GetApparentRaDecJNow(new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc));

        Assert.InRange(raHours, 0, 24);
        Assert.InRange(decDeg, -90, 90);
    }

    private static double NormalizeHours(double hours)
    {
        var result = hours % 24.0;
        return result < 0 ? result + 24.0 : result;
    }
}
