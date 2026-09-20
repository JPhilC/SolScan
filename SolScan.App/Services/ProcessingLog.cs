using System.IO;
using SolScan.Core.Processing;

namespace SolScan.App.Services;

/// <summary>
/// A single Process run's plain-text log, in the spirit of JSol'Ex's own per-run log file: timestamped
/// <c>HH:mm:ss.fff [LEVEL] message</c> lines, written to a numbered file in the source recording's own
/// "log" output subfolder (<see cref="ProcessingLocations.GetLogsFolder"/> - a peer of the existing
/// "raw"/"processed" subfolders, though "log" isn't itself a <see cref="DirectoryKind"/>, since SolScan
/// generates no image kind that lives there).
///
/// Deliberately not a Core-interface/Infrastructure-implementation pair the way SolScan's other stores
/// (<see cref="SolScan.Core.Capture.ICaptureMetadataStore"/>, etc.) are - this is a single sequential
/// text-file writer with no swappable implementation, hardware dependency, or test double anywhere, so
/// that ceremony would be pure abstraction with nothing to abstract over. Matches
/// <c>ProcessViewModel</c>'s own existing <c>SavePng</c>/<c>SaveColorPng</c>, which already write files
/// directly the same way rather than through an injected interface.
///
/// Content is scoped to what SolScan's own pipeline actually computes - things like JSol'Ex's Carrington
/// rotation/B0/L0/P solar-ephemeris line, parallactic angle, disk diameter, and per-batch memory/
/// throughput figures have no SolScan equivalent (different feature scope entirely, or - for
/// throughput - no per-stage instrumentation to report it honestly) and are simply not present, rather
/// than being invented to look complete.
/// </summary>
public sealed class ProcessingLog : IDisposable
{
    private readonly StreamWriter _writer;

    /// <summary>Opens the next-numbered log file for <paramref name="serFilePath"/> - scans the
    /// existing "log" subfolder for the highest already-used <c>NNNN_</c> prefix and starts one past it
    /// (0000 if none exist yet), so re-processing the same file keeps every past run's own log rather
    /// than overwriting it. "One past the highest used", not "count of files present", so a manually
    /// deleted log in the middle of the sequence doesn't get its number reused.</summary>
    public static ProcessingLog OpenNext(string serFilePath)
    {
        var folder = ProcessingLocations.GetLogsFolder(serFilePath);
        Directory.CreateDirectory(folder);

        var baseName = Path.GetFileNameWithoutExtension(serFilePath);
        var nextSequence = 0;
        foreach (var existingPath in Directory.EnumerateFiles(folder, "*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(existingPath);
            var underscoreIndex = name.IndexOf('_');
            if (underscoreIndex > 0 && int.TryParse(name[..underscoreIndex], out var sequence) && sequence >= nextSequence)
            {
                nextSequence = sequence + 1;
            }
        }

        var path = Path.Combine(folder, $"{nextSequence:D4}_{baseName}.log");
        return new ProcessingLog(path);
    }

    /// <summary>Opens (creating its folder if needed) a log at an explicit <paramref name="path"/> -
    /// for callers outside the per-recording numbered scheme above, e.g. Capture's Find Sun log.</summary>
    public static ProcessingLog OpenAt(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new ProcessingLog(path);
    }

    private ProcessingLog(string path)
    {
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    public void Info(string message) => WriteLine("INFO", message);

    public void Error(string message) => WriteLine("ERROR", message);

    /// <summary>A plain indented continuation line with no timestamp/level prefix - matches JSol'Ex's
    /// own multi-line blocks (e.g. a distortion polynomial's individual coefficients printed under one
    /// "Distortion polynomial..." heading line).</summary>
    public void Detail(string message) => _writer.WriteLine($"   {message}");

    private void WriteLine(string level, string message) =>
        _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");

    public void Dispose() => _writer.Dispose();
}
