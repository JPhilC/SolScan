namespace SolScan.Processing.Spectrum;

/// <summary>
/// A 1D intensity profile along the dispersion axis, indexed by pixel-shift from the fitted spectral
/// line's own centre row (0 = the line's own centre, negative/positive = above/below it) - the shape
/// <see cref="SpectralProfileExtractor.Extract"/> produces and <see cref="SpectralLineIdentifier"/>
/// consumes. Deliberately in pixel-shift units, not Å - converting to Å needs a dispersion figure
/// (<see cref="SolScan.Core.Processing.SpectralDispersion"/>), which depends on which line is actually
/// being tested as a hypothesis, so that conversion happens per-candidate inside the identifier, not
/// once here.
/// </summary>
/// <param name="Values">One averaged intensity value per pixel-shift from <see cref="MinShiftPixels"/>
/// to <see cref="MinShiftPixels"/> + Values.Count - 1 inclusive. A shift with no in-bounds source rows
/// anywhere across the frame's width is <see cref="double.NaN"/> - see <see cref="SpectralProfileExtractor"/>.</param>
/// <param name="MinShiftPixels">The pixel-shift the first entry of <see cref="Values"/> represents.</param>
public sealed record SpectralProfile(IReadOnlyList<double> Values, int MinShiftPixels)
{
    /// <summary>The pixel-shift the last entry of <see cref="Values"/> represents.</summary>
    public int MaxShiftPixels => MinShiftPixels + Values.Count - 1;

    /// <returns>The value at <paramref name="shiftPixels"/>, or null if it's outside
    /// [<see cref="MinShiftPixels"/>, <see cref="MaxShiftPixels"/>] or itself <see cref="double.NaN"/>.</returns>
    public double? ValueAt(int shiftPixels)
    {
        if (shiftPixels < MinShiftPixels || shiftPixels > MaxShiftPixels)
        {
            return null;
        }

        var value = Values[shiftPixels - MinShiftPixels];
        return double.IsNaN(value) ? null : value;
    }
}
