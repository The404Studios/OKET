using System.Runtime.InteropServices;
using OKET.Core.Types;
using OKET.Core.Interfaces;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace OKET.Vision.Capture;

/// <summary>
/// High-performance frame capture using DXGI Desktop Duplication API.
/// Captures frames directly from the GPU with minimal latency.
/// </summary>
public sealed class DxgiFrameSource : IFrameSource
{
    private Device? _device;
    private OutputDuplication? _duplication;
    private Texture2D? _stagingTexture;

    private long _frameId;
    private int _width;
    private int _height;
    private bool _isCapturing;
    private readonly object _lock = new();

    private DateTime _lastFrameTime = DateTime.UtcNow;
    private float _currentFps;
    private int _frameCount;
    private DateTime _fpsCounterStart = DateTime.UtcNow;

    public bool IsCapturing => _isCapturing;
    public string WindowTitle { get; private set; } = "Garry's Mod";
    public (int Width, int Height) Resolution => (_width, _height);
    public float CurrentFps => _currentFps;

    /// <summary>
    /// Index of the display adapter to capture from.
    /// </summary>
    public int AdapterIndex { get; set; } = 0;

    /// <summary>
    /// Index of the output (monitor) to capture from.
    /// </summary>
    public int OutputIndex { get; set; } = 0;

    /// <summary>
    /// Timeout for acquiring frames (ms).
    /// </summary>
    public int AcquireTimeoutMs { get; set; } = 100;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await Task.Run(() => Initialize(), ct);
    }

    private void Initialize()
    {
        lock (_lock)
        {
            if (_isCapturing) return;

            // Create D3D11 device
            _device = new Device(SharpDX.Direct3D.DriverType.Hardware,
                DeviceCreationFlags.BgraSupport);

            // Get DXGI adapter and output
            using var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device>();
            using var adapter = dxgiDevice.Adapter;
            using var factory = adapter.GetParent<Factory1>();
            using var adapter1 = factory.GetAdapter1(AdapterIndex);
            using var output = adapter1.GetOutput(OutputIndex);
            using var output1 = output.QueryInterface<Output1>();

            // Get output description for resolution
            var desc = output.Description;
            _width = desc.DesktopBounds.Right - desc.DesktopBounds.Left;
            _height = desc.DesktopBounds.Bottom - desc.DesktopBounds.Top;

            // Create output duplication
            _duplication = output1.DuplicateOutput(_device);

            // Create staging texture for CPU access
            var textureDesc = new Texture2DDescription
            {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None
            };
            _stagingTexture = new Texture2D(_device, textureDesc);

            _isCapturing = true;
            _frameId = 0;
        }
    }

    public Task StopAsync()
    {
        lock (_lock)
        {
            _isCapturing = false;

            _stagingTexture?.Dispose();
            _stagingTexture = null;

            _duplication?.Dispose();
            _duplication = null;

            _device?.Dispose();
            _device = null;
        }

        return Task.CompletedTask;
    }

    public Frame? GetFrame()
    {
        if (!_isCapturing || _duplication == null || _device == null || _stagingTexture == null)
            return null;

        lock (_lock)
        {
            try
            {
                // Try to acquire next frame
                var result = _duplication.TryAcquireNextFrame(
                    AcquireTimeoutMs,
                    out var frameInfo,
                    out var desktopResource);

                if (result.Failure)
                {
                    return null;
                }

                try
                {
                    // Get the desktop texture
                    using var desktopTexture = desktopResource.QueryInterface<Texture2D>();

                    // Copy to staging texture
                    _device.ImmediateContext.CopyResource(desktopTexture, _stagingTexture);

                    // Map the staging texture for CPU read
                    var dataBox = _device.ImmediateContext.MapSubresource(
                        _stagingTexture, 0, MapMode.Read, MapFlags.None);

                    try
                    {
                        // Copy pixel data
                        var data = new byte[_width * _height * 4];
                        var sourcePtr = dataBox.DataPointer;
                        var rowPitch = dataBox.RowPitch;

                        for (int y = 0; y < _height; y++)
                        {
                            Marshal.Copy(
                                sourcePtr + y * rowPitch,
                                data,
                                y * _width * 4,
                                _width * 4);
                        }

                        var frame = new Frame(
                            _frameId++,
                            DateTime.UtcNow,
                            _width,
                            _height,
                            data);

                        UpdateFpsCounter();

                        return frame;
                    }
                    finally
                    {
                        _device.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
                    }
                }
                finally
                {
                    desktopResource?.Dispose();
                    _duplication.ReleaseFrame();
                }
            }
            catch (SharpDXException ex) when (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)
            {
                // No new frame available
                return null;
            }
            catch (SharpDXException ex) when (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.AccessLost.Result.Code)
            {
                // Desktop mode changed, need to reinitialize
                Reinitialize();
                return null;
            }
        }
    }

    private void Reinitialize()
    {
        _duplication?.Dispose();
        _duplication = null;
        _stagingTexture?.Dispose();
        _stagingTexture = null;
        _isCapturing = false;

        // Will reinitialize on next GetFrame call
        try
        {
            Initialize();
        }
        catch
        {
            // Initialization failed, will retry later
        }
    }

    private void UpdateFpsCounter()
    {
        _frameCount++;
        var now = DateTime.UtcNow;
        var elapsed = (now - _fpsCounterStart).TotalSeconds;

        if (elapsed >= 1.0)
        {
            _currentFps = (float)(_frameCount / elapsed);
            _frameCount = 0;
            _fpsCounterStart = now;
        }

        _lastFrameTime = now;
    }

    public void Dispose()
    {
        StopAsync().Wait();
    }
}
