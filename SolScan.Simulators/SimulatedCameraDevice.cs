using SolScan.Core.Camera;

namespace SolScan.Simulators;

/// <summary>
/// Fake <see cref="ICameraDevice"/> needing no hardware or vendor SDK at all - what
/// <see cref="SimulatedCameraProvider"/> hands out, and what the Capture view/tests exercise by
/// default. Generates a synthetic drifting bright band (standing in for the solar disk's edge
/// crossing the SHG's slit) so live-preview/recording plumbing has something to actually show
/// while developing without ZWO/Altair hardware attached - see SolScan CLAUDE.md.
/// </summary>
public sealed class SimulatedCameraDevice : ICameraDevice
{
    private const int NativeWidth = 640;
    private const int NativeHeight = 480;
    private const double TargetFps = 15;

    private readonly Random _random = new();
    private Thread? _generatorThread;
    private volatile bool _stopRequested;
    private double _bandPositionX;

    private int _width = NativeWidth;
    private int _height = NativeHeight;
    private int _bitDepth = 16;
    private CameraOutputFormat _outputFormat = CameraOutputFormat.Mono16;
    private int _binning = 1;

    public SimulatedCameraDevice(string id = "simulated-1", string name = "Simulated Camera")
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }

    public bool IsConnected { get; private set; }
    public bool IsStreaming { get; private set; }

    /// <summary>A plausible constant (ASI678MM-ballpark), not read from anything real - so the
    /// camera-profile auto-add flow (see CaptureViewModel) is exercisable/testable without real
    /// hardware attached.</summary>
    public double? PixelSizeMicrons => 2.0;

    /// <summary>Accepted and reflected in the generated frame's brightness, but not backed by any
    /// real sensor - purely so the Gain slider visibly does something during dev/testing. Range
    /// matches the ASI678MM (0-600) so the slider behaves the same regardless of which camera is
    /// selected.</summary>
    public double Gain { get; set; } = 150;

    public double ExposureMicroseconds { get; set; } = 10_000;
    public int UsbBandwidthPercent { get; set; } = 80;

    /// <summary>No real auto-anything to drive - these just hold whatever the UI last set, purely
    /// so the Capture view's Auto toggles have something to bind to during dev/testing.</summary>
    public bool IsGainAuto { get; set; }

    public bool IsExposureAuto { get; set; }
    public bool IsUsbBandwidthAuto { get; set; }

    public CameraOutputFormat OutputFormat => _outputFormat;
    public int Binning => _binning;
    public IReadOnlyList<int> SupportedBinning { get; } = [1, 2, 3, 4];

    public long DroppedFrameCount => 0;

    public event EventHandler<CameraFrame>? FrameCaptured;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsStreaming)
        {
            StopStreamingAsync(cancellationToken).GetAwaiter().GetResult();
        }

        IsConnected = false;
        return Task.CompletedTask;
    }

    /// <summary>Mirrors the real devices' shape - frame Width/Height shrink with Binning and now
    /// with roiWidth/roiHeight too (see <see cref="FramePreview.ComputeCenteredRoi"/>, matching
    /// AsiCameraDevice's own approach), BitDepth follows OutputFormat - purely so the Capture view's
    /// colour-space/binning/ROI controls have a visible effect during dev/testing without real
    /// hardware.</summary>
    public async Task SetOutputFormatAsync(CameraOutputFormat outputFormat, int binning, int roiWidth = 0, int roiHeight = 0, CancellationToken cancellationToken = default)
    {
        var wasStreaming = IsStreaming;
        if (wasStreaming)
        {
            await StopStreamingAsync(cancellationToken);
        }

        _outputFormat = outputFormat;
        _binning = binning;
        var fullWidth = NativeWidth / binning;
        var fullHeight = NativeHeight / binning;
        var roi = FramePreview.ComputeCenteredRoi(fullWidth, fullHeight, roiWidth, roiHeight);
        _width = roi.Width;
        _height = roi.Height;
        _bitDepth = outputFormat == CameraOutputFormat.Mono16 ? 16 : 8;

        if (wasStreaming)
        {
            await StartStreamingAsync(cancellationToken);
        }
    }

    public Task StartStreamingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected || IsStreaming)
        {
            return Task.CompletedTask;
        }

        _stopRequested = false;
        IsStreaming = true;
        _generatorThread = new Thread(GenerateLoop)
        {
            IsBackground = true,
            Name = "SimulatedCameraDevice frame generator",
        };
        _generatorThread.Start();
        return Task.CompletedTask;
    }

    public Task StopStreamingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsStreaming)
        {
            return Task.CompletedTask;
        }

        _stopRequested = true;
        _generatorThread?.Join();
        _generatorThread = null;
        IsStreaming = false;
        return Task.CompletedTask;
    }

    private void GenerateLoop()
    {
        var frameIntervalMs = (int)(1000 / TargetFps);
        while (!_stopRequested)
        {
            FrameCaptured?.Invoke(this, GenerateFrame());
            Thread.Sleep(frameIntervalMs);
        }
    }

    private CameraFrame GenerateFrame()
    {
        var width = _width;
        var height = _height;
        var bitDepth = _bitDepth;
        var maxValue = (1 << bitDepth) - 1;

        _bandPositionX = (_bandPositionX + 0.5 / _binning) % width;
        var bandWidth = 40.0 / _binning;
        var brightness = maxValue * Math.Clamp(Gain / 600.0, 0.05, 1.0);

        var samples = new int[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var distance = Math.Abs(x - _bandPositionX);
                var signal = distance < bandWidth ? brightness * (1 - (distance / bandWidth)) : 0;
                var noise = _random.NextDouble() * brightness * 0.05;
                samples[(y * width) + x] = (int)Math.Clamp(signal + noise, 0, maxValue);
            }
        }

        var bytesPerPixel = bitDepth > 8 ? 2 : 1;
        var bytes = new byte[samples.Length * bytesPerPixel];
        if (bytesPerPixel == 2)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                bytes[i * 2] = (byte)samples[i];
                bytes[(i * 2) + 1] = (byte)(samples[i] >> 8);
            }
        }
        else
        {
            for (var i = 0; i < samples.Length; i++)
            {
                bytes[i] = (byte)samples[i];
            }
        }

        return new CameraFrame(bytes, width, height, bitDepth, DateTime.UtcNow);
    }
}
