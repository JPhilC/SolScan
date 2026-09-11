using SolScan.Core.Camera;

namespace SolScan.Tests;

public class CameraDiscoveryServiceTests
{
    [Fact]
    public void DiscoverAll_ConcatenatesEveryProvider()
    {
        var providerA = new FakeCameraProvider([new FakeCameraDevice("a1"), new FakeCameraDevice("a2")]);
        var providerB = new FakeCameraProvider([new FakeCameraDevice("b1")]);

        var service = new CameraDiscoveryService([providerA, providerB]);

        var result = service.DiscoverAll();

        Assert.Equal(3, result.Count);
        Assert.Contains(result, d => d.Id == "a1");
        Assert.Contains(result, d => d.Id == "b1");
    }

    [Fact]
    public void DiscoverAll_SurvivesOneProviderReturningNothing()
    {
        var empty = new FakeCameraProvider([]);
        var withDevices = new FakeCameraProvider([new FakeCameraDevice("only")]);

        var service = new CameraDiscoveryService([empty, withDevices]);

        var result = service.DiscoverAll();

        Assert.Single(result);
        Assert.Equal("only", result[0].Id);
    }

    private sealed class FakeCameraProvider(IReadOnlyList<ICameraDevice> devices) : ICameraProvider
    {
        public string VendorName => "Fake";
        public IReadOnlyList<ICameraDevice> Discover() => devices;
    }

    private sealed class FakeCameraDevice(string id) : ICameraDevice
    {
        public string Id => id;
        public string Name => id;
        public bool IsConnected => false;
        public bool IsStreaming => false;
        public double? PixelSizeMicrons => null;
        public double Gain { get; set; }
        public double ExposureMicroseconds { get; set; }
        public int UsbBandwidthPercent { get; set; }
        public bool IsGainAuto { get; set; }
        public bool IsExposureAuto { get; set; }
        public bool IsUsbBandwidthAuto { get; set; }
        public CameraOutputFormat OutputFormat { get; private set; }
        public int Binning { get; private set; } = 1;
        public IReadOnlyList<int> SupportedBinning { get; } = [1, 2, 3, 4];
        public long DroppedFrameCount => 0;

        public Task SetOutputFormatAsync(CameraOutputFormat outputFormat, int binning, int roiWidth = 0, int roiHeight = 0, CancellationToken cancellationToken = default)
        {
            OutputFormat = outputFormat;
            Binning = binning;
            return Task.CompletedTask;
        }

        public event EventHandler<CameraFrame>? FrameCaptured
        {
            add { }
            remove { }
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartStreamingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopStreamingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
