namespace SolScan.Core.Processing;

/// <summary>Where SolScan puts processing output, absent any user customization - mirrors
/// <see cref="SolScan.Core.Capture.CaptureLocations"/>'s own "conventions, not settings" role.</summary>
public static class ProcessingLocations
{
    /// <summary>The output folder for a given <c>.ser</c> capture: a folder sitting alongside the
    /// file, named after it - e.g. <c>...\SolScan_20250831_161427.ser</c> →
    /// <c>...\SolScan_20250831_161427\</c>.</summary>
    public static string GetOutputFolder(string serFilePath)
    {
        var directory = Path.GetDirectoryName(serFilePath);
        var baseName = Path.GetFileNameWithoutExtension(serFilePath);
        return string.IsNullOrEmpty(directory) ? baseName : Path.Combine(directory, baseName);
    }

    /// <summary>Where a given <paramref name="directoryKind"/>'s output images live within
    /// <see cref="GetOutputFolder"/> - e.g. <see cref="DirectoryKind.Raw"/> images go in a
    /// <c>raw</c> subfolder, <see cref="DirectoryKind.Processed"/> ones in <c>processed</c>. Matches
    /// real JSolex's own layout exactly (<c>NamingStrategyAwareImageEmitter.java</c>'s
    /// <c>kind.directoryKind().name().toLowerCase(...)</c> - the folder name is just the enum
    /// constant's own name, lower-cased), so SolScan's output coexists cleanly with JSolex's own if
    /// both are ever pointed at the same recording. See <see cref="GeneratedImageKindExtensions.GetDirectoryKind"/>
    /// for which <see cref="GeneratedImageKind"/> maps to which <see cref="DirectoryKind"/>.</summary>
    public static string GetImagesFolder(string serFilePath, DirectoryKind directoryKind) =>
        Path.Combine(GetOutputFolder(serFilePath), directoryKind.ToString().ToLowerInvariant());
}
