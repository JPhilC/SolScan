namespace SolScan.Core.Astronomy;

/// <summary>
/// Low-precision analytic solar ephemeris - geometric/apparent Sun position accurate to roughly
/// 0.01° (~36 arcsec) between 1950-2050, per Meeus, "Astronomical Algorithms" ch. 25 ("Low precision
/// formulas for the Sun"). Plenty for SolScan's own needs: the initial "Find Sun" slew only has to
/// land the Sun somewhere inside a live camera frame - a camera-based fine-tune then takes over from
/// there (see SolScan.App.ViewModels.CaptureViewModel.FindSunAsync) - rather than needing arcsecond
/// precision itself. See CLAUDE.md Phase 3, "Find the sun".
///
/// Deliberately geocentric, not topocentric: the Sun's parallax is only ~8.8 arcsec even from the
/// most favourable site, an order of magnitude below this algorithm's own ~36 arcsec accuracy, so
/// site latitude/longitude/elevation would add complexity without adding anything usable here.
/// </summary>
public static class SunPosition
{
    /// <summary>
    /// The Sun's apparent right ascension/declination at the equinox and equator *of date* ("JNow") -
    /// the same coordinate frame <see cref="Telescope.ITelescopeMount"/>'s Get/SlewToCoordinatesAsync
    /// already use, so the result can be handed straight to <c>SlewToCoordinatesAsync</c> with no
    /// further conversion.
    /// </summary>
    /// <param name="utcNow">The instant to compute the position for, in UTC (a local <see cref="DateTime"/>
    /// is converted automatically).</param>
    public static (double RaHours, double DecDeg) GetApparentRaDecJNow(DateTime utcNow)
    {
        if (utcNow.Kind == DateTimeKind.Local)
        {
            utcNow = utcNow.ToUniversalTime();
        }

        var t = (ToJulianDay(utcNow) - 2451545.0) / 36525.0; // Julian centuries since J2000.0

        // Geometric mean longitude and mean anomaly of the Sun (degrees).
        var l0 = NormalizeDegrees(280.46646 + (t * (36000.76983 + (t * 0.0003032))));
        var m = NormalizeDegrees(357.52911 + (t * (35999.05029 - (t * 0.0001537))));
        var mRad = DegToRad(m);

        // Equation of centre - the Sun's true longitude minus its mean longitude.
        var c = ((1.914602 - (t * (0.004817 + (0.000014 * t)))) * Math.Sin(mRad))
            + ((0.019993 - (0.000101 * t)) * Math.Sin(2 * mRad))
            + (0.000289 * Math.Sin(3 * mRad));

        var trueLongitude = l0 + c;

        // Apparent longitude - a short-form nutation-in-longitude + aberration correction via the
        // Moon's mean ascending node, rather than a full nutation series.
        var omega = 125.04 - (1934.136 * t);
        var apparentLongitude = trueLongitude - 0.00569 - (0.00478 * Math.Sin(DegToRad(omega)));
        var lambdaRad = DegToRad(apparentLongitude);

        // Mean obliquity of the ecliptic, corrected for the same nutation term used above.
        var meanObliquity = 23.0 + (26.0 / 60.0) + (21.448 / 3600.0)
            - ((t * (46.8150 + (t * (0.00059 - (0.001813 * t))))) / 3600.0);
        var obliquity = meanObliquity + (0.00256 * Math.Cos(DegToRad(omega)));
        var epsilonRad = DegToRad(obliquity);

        var raRad = Math.Atan2(Math.Cos(epsilonRad) * Math.Sin(lambdaRad), Math.Cos(lambdaRad));
        var decRad = Math.Asin(Math.Sin(epsilonRad) * Math.Sin(lambdaRad));

        var raHours = NormalizeHours(RadToDeg(raRad) / 15.0);
        var decDeg = RadToDeg(decRad);

        return (raHours, decDeg);
    }

    /// <summary>Standard Julian Day for a Gregorian-calendar UTC <see cref="DateTime"/> (Meeus ch. 7).</summary>
    private static double ToJulianDay(DateTime utc)
    {
        var year = utc.Year;
        var month = utc.Month;
        var dayFraction = ((utc.Hour * 3600) + (utc.Minute * 60) + utc.Second + (utc.Millisecond / 1000.0)) / 86400.0;
        var day = utc.Day + dayFraction;

        if (month <= 2)
        {
            year -= 1;
            month += 12;
        }

        var a = year / 100;
        var b = 2 - a + (a / 4);

        return Math.Floor(365.25 * (year + 4716)) + Math.Floor(30.6001 * (month + 1)) + day + b - 1524.5;
    }

    private static double NormalizeDegrees(double degrees)
    {
        var result = degrees % 360.0;
        return result < 0 ? result + 360.0 : result;
    }

    private static double NormalizeHours(double hours)
    {
        var result = hours % 24.0;
        return result < 0 ? result + 24.0 : result;
    }

    private static double DegToRad(double degrees) => degrees * Math.PI / 180.0;

    private static double RadToDeg(double radians) => radians * 180.0 / Math.PI;
}
