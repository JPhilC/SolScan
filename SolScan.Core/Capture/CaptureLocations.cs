namespace SolScan.Core.Capture;

/// <summary>Where SolScan puts things related to capture, absent any user customization.</summary>
public static class CaptureLocations
{
    /// <summary>SolScan's own default location for new .ser recordings when
    /// <see cref="AppSettings.CapturesRootFolder"/> hasn't been customized -
    /// <c>Documents\SolScan\Captures</c>. A property, not a const, since it depends on
    /// <see cref="Environment.GetFolderPath"/> (the current user's actual Documents folder).</summary>
    public static string DefaultCapturesRootFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SolScan", "Captures");
}
