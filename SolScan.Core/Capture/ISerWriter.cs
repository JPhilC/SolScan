using SolScan.Core.Camera;

namespace SolScan.Core.Capture;

/// <summary>
/// Writes a live frame stream out to a .ser file - the format JSolex/SharpCap/Sol'ex tooling all
/// consume, so a SolScan capture drops straight into that existing ecosystem. Implemented in
/// SolScan.Infrastructure; the SER layout itself is simple enough to write from scratch rather
/// than depend on a third-party library.
/// </summary>
public interface ISerWriter : IDisposable
{
    void Open(string path, int width, int height, int bitDepth);
    void WriteFrame(CameraFrame frame);
    void Close();
}
