using System.Threading;
using SolScan.Core.Camera;
using SolScan.Simulators;

namespace SolScan.Tests;

public class SimulatedCameraDeviceTests
{
    [Fact]
    public async Task StreamingRaisesFrameCapturedWithExpectedGeometry()
    {
        var device = new SimulatedCameraDevice();
        CameraFrame? received = null;
        using var frameReceived = new SemaphoreSlim(0);

        device.FrameCaptured += (_, frame) =>
        {
            received = frame;
            frameReceived.Release();
        };

        await device.ConnectAsync();
        await device.StartStreamingAsync();
        var signalled = await frameReceived.WaitAsync(TimeSpan.FromSeconds(5));
        await device.StopStreamingAsync();
        await device.DisconnectAsync();

        Assert.True(signalled, "Expected FrameCaptured to fire within 5 seconds of starting streaming.");
        Assert.NotNull(received);
        Assert.Equal(640, received!.Width);
        Assert.Equal(480, received.Height);
        Assert.Equal(16, received.BitDepth);
        Assert.Equal(640 * 480 * 2, received.Data.Length);
        Assert.False(device.IsStreaming);
    }

    [Fact]
    public async Task SetOutputFormatAsync_ShrinksFrameGeometryByBinningAndFollowsColorSpace()
    {
        var device = new SimulatedCameraDevice();
        CameraFrame? received = null;
        using var frameReceived = new SemaphoreSlim(0);

        device.FrameCaptured += (_, frame) =>
        {
            received = frame;
            frameReceived.Release();
        };

        await device.ConnectAsync();
        await device.SetOutputFormatAsync(CameraOutputFormat.Mono8, binning: 2);
        await device.StartStreamingAsync();
        var signalled = await frameReceived.WaitAsync(TimeSpan.FromSeconds(5));
        await device.StopStreamingAsync();
        await device.DisconnectAsync();

        Assert.True(signalled, "Expected FrameCaptured to fire within 5 seconds of starting streaming.");
        Assert.NotNull(received);
        Assert.Equal(320, received!.Width); // 640 / binning
        Assert.Equal(240, received.Height); // 480 / binning
        Assert.Equal(8, received.BitDepth);
        Assert.Equal(320 * 240, received.Data.Length); // 1 byte/pixel in Mono8
        Assert.Equal(CameraOutputFormat.Mono8, device.OutputFormat);
        Assert.Equal(2, device.Binning);
    }
}
