using SolScan.Core.Processing;
using SolScan.Processing.Spectrum;

namespace SolScan.Tools;

/// <summary>
/// Reads BASS2000's raw <c>atlasvi.dat</c> file format directly and extracts a narrow reference
/// window around each of the 12 named <see cref="SpectralRay"/> lines - what the <c>extract-atlas</c>
/// command uses to (re)build SolScan.Processing's bundled reference-window resource.
///
/// This is an independently-written reader, not a port of astro4j's own <c>SpectrumFileConverter</c>
/// (<c>build-logic/.../SpectrumFileConverter.java</c>) - the raw format itself was understood by
/// reading that converter once, then confirmed directly against the real local
/// <c>jsolex-core/src/bass2000/atlasvi.dat</c> file (8 header lines; each subsequent line is one
/// whitespace-separated record whose first token is an integer wavelength in Å, incrementing by
/// exactly 1 per line, and whose *last* token is a fixed 2000-character digit blob - 500 four-digit
/// (0-9999) intensity samples spaced 0.002Å apart, i.e. 500 samples per 1Å line - everything between
/// those two tokens is calibration metadata this reader ignores). "How to read a public data file's
/// own fixed layout" isn't the algorithmic design worth keeping independent - there's only one correct
/// way to parse a file that has this exact structure, unlike the identification algorithm itself (see
/// <see cref="SpectralLineIdentifier"/>'s own doc comment).
/// </summary>
public static class AtlasExtractor
{
    private const int HeaderLinesToSkip = 8;

    /// <summary>Confirmed directly against the real file: 500 four-digit samples span exactly 1Å.</summary>
    private const double NativeStepAngstroms = 1.0 / 500.0;

    /// <summary>Half-width of each extracted reference window - generous enough to comfortably cover
    /// a line's own core, wings, and continuum margin at typical SHG dispersions.</summary>
    public const double HalfWidthAngstroms = 8.0;

    /// <summary>Output resampling step - matches astro4j's own atlas resolution (a reasonable, not
    /// arbitrary, choice: real SHG dispersion is on the order of tens of mÅ/pixel, so 0.01Å reference
    /// resolution resolves several reference samples per observed pixel).</summary>
    public const double OutputStepAngstroms = 0.01;

    public static IReadOnlyList<ReferenceWindow> ExtractWindows(string atlasPath, IProgress<string>? progress = null)
    {
        var targets = SpectralRay.Predefined.Where(r => r.WavelengthAngstroms > 0).ToList();
        var rawSamples = targets.ToDictionary(r => r, _ => new List<(double Wavelength, double Intensity)>());

        progress?.Report($"Reading '{atlasPath}'...");
        using (var reader = new StreamReader(atlasPath))
        {
            for (var i = 0; i < HeaderLinesToSkip; i++)
            {
                reader.ReadLine();
            }

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !double.TryParse(parts[0], out var lineWavelength))
                {
                    continue;
                }

                var relevantTargets = targets.Where(r => LineOverlapsTarget(lineWavelength, r.WavelengthAngstroms)).ToList();
                if (relevantTargets.Count == 0)
                {
                    continue;
                }

                var blob = parts[^1];
                for (var k = 0; (k * 4) + 4 <= blob.Length; k++)
                {
                    if (!int.TryParse(blob.AsSpan(k * 4, 4), out var intensity))
                    {
                        continue;
                    }

                    var wavelength = lineWavelength + (k * NativeStepAngstroms);
                    foreach (var ray in relevantTargets)
                    {
                        if (System.Math.Abs(wavelength - ray.WavelengthAngstroms) <= HalfWidthAngstroms + 1)
                        {
                            rawSamples[ray].Add((wavelength, intensity));
                        }
                    }
                }
            }
        }

        var windows = new List<ReferenceWindow>();
        foreach (var ray in targets)
        {
            var samples = rawSamples[ray].OrderBy(s => s.Wavelength).ToList();
            if (samples.Count < 2)
            {
                throw new InvalidDataException($"No atlas data found for {ray.Label} ({ray.WavelengthAngstroms}Å) in '{atlasPath}'.");
            }

            progress?.Report($"Resampling {ray.Label} ({samples.Count} raw samples)...");
            windows.Add(new ReferenceWindow(ray.WavelengthAngstroms, OutputStepAngstroms,
                Resample(samples, ray.WavelengthAngstroms, HalfWidthAngstroms, OutputStepAngstroms)));
        }

        return windows;
    }

    /// <summary>Does this data line's own 1Å span come within 1Å of the target's own ±<see cref="HalfWidthAngstroms"/>
    /// window (the extra 1Å is interpolation margin for <see cref="Resample"/>'s own edges)?</summary>
    private static bool LineOverlapsTarget(double lineWavelength, double targetWavelength) =>
        lineWavelength <= targetWavelength + HalfWidthAngstroms + 1 && lineWavelength + 1 >= targetWavelength - HalfWidthAngstroms - 1;

    private static double[] Resample(IReadOnlyList<(double Wavelength, double Intensity)> samples, double center, double halfWidth, double step)
    {
        var count = (int)System.Math.Round((2 * halfWidth / step) + 1);
        var result = new double[count];
        var searchIndex = 0;

        for (var i = 0; i < count; i++)
        {
            var wavelength = center - halfWidth + (i * step);
            while (searchIndex < samples.Count - 2 && samples[searchIndex + 1].Wavelength <= wavelength)
            {
                searchIndex++;
            }

            var (w0, v0) = samples[searchIndex];
            var (w1, v1) = samples[System.Math.Min(searchIndex + 1, samples.Count - 1)];
            var span = w1 - w0;
            result[i] = span > 0 ? v0 + ((v1 - v0) * (wavelength - w0) / span) : v0;
        }

        return result;
    }
}
