using System.Reflection;
using System.Text;

namespace SolScan.Processing.Spectrum;

/// <summary>
/// Reads/writes the small bundled reference-window dataset <see cref="SpectralLineIdentifier"/> uses -
/// one <see cref="ReferenceWindow"/> per named <see cref="SolScan.Core.Processing.SpectralRay"/>, a
/// narrow (~16Å) slice of the public BASS2000 solar flux atlas around each line's own wavelength, not
/// the 14MB atlas itself (see this project's own tools - <c>SolScan.Tools</c>'s <c>extract-atlas</c>
/// command - for how this file is produced; <see cref="Resources"/>'s own <c>README.md</c> for the
/// source atlas's licensing). A simple custom binary layout (4-byte magic, window count, then per
/// window: centre Å, step Å, sample count, and the samples themselves as <see cref="ushort"/> - the
/// source atlas's own native 0-9999 intensity scale, matching astro4j's own <c>short[]</c> storage for
/// the same reason: compact, and the scale is never used except relatively) - plain
/// <see cref="BinaryWriter"/>/<see cref="BinaryReader"/>, same convention as
/// <see cref="SolScan.Core.Capture.ISerWriter"/>/<see cref="SolScan.Core.Capture.ISerReader"/> rather
/// than pulling in a serialization library for what's a small, fixed, internal format.
/// </summary>
public static class ReferenceWindowResource
{
    private const string Magic = "SRW1";
    private const string EmbeddedResourceName = "SolScan.Processing.Spectrum.Resources.reference-windows.bin";

    public static void Save(Stream stream, IReadOnlyList<ReferenceWindow> windows)
    {
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes(Magic));
        writer.Write(windows.Count);

        foreach (var window in windows)
        {
            writer.Write(window.CenterWavelengthAngstroms);
            writer.Write(window.StepAngstroms);
            writer.Write(window.Intensities.Count);
            foreach (var intensity in window.Intensities)
            {
                writer.Write((ushort)System.Math.Clamp(intensity, 0, ushort.MaxValue));
            }
        }
    }

    public static IReadOnlyList<ReferenceWindow> Load(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (magic != Magic)
        {
            throw new InvalidDataException($"Not a SolScan reference-window resource (expected magic '{Magic}', got '{magic}').");
        }

        var count = reader.ReadInt32();
        var windows = new List<ReferenceWindow>(count);
        for (var i = 0; i < count; i++)
        {
            var centerWavelength = reader.ReadDouble();
            var step = reader.ReadDouble();
            var sampleCount = reader.ReadInt32();
            var intensities = new double[sampleCount];
            for (var s = 0; s < sampleCount; s++)
            {
                intensities[s] = reader.ReadUInt16();
            }

            windows.Add(new ReferenceWindow(centerWavelength, step, intensities));
        }

        return windows;
    }

    /// <summary>Loads the resource bundled into this assembly at build time - what
    /// <see cref="SpectralLineIdentifier"/> actually uses; <see cref="Load(Stream)"/>/<see cref="Save"/>
    /// are the reusable primitives underneath, exercised directly by tests and by the
    /// <c>extract-atlas</c> tool without needing a real embedded resource.</summary>
    public static IReadOnlyList<ReferenceWindow> LoadEmbedded()
    {
        var assembly = typeof(ReferenceWindowResource).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{EmbeddedResourceName}' not found in {assembly.FullName}.");
        return Load(stream);
    }
}
