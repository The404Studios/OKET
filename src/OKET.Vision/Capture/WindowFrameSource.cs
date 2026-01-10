using System.Diagnostics;
using System.Runtime.InteropServices;
using OKET.Core.Types;
using OKET.Core.Interfaces;

namespace OKET.Vision.Capture;

/// <summary>
/// Fallback frame source using GDI+ BitBlt.
/// Slower than DXGI but more compatible.
/// </summary>
public sealed class WindowFrameSource : IFrameSource
{
    private IntPtr _windowHandle;
    private int _width;
    private int _height;
    private long _frameId;
    private bool _isCapturing;
    private readonly object _lock = new();

    private float _currentFps;
    private int _frameCount;
    private DateTime _fpsCounterStart = DateTime.UtcNow;

    public bool IsCapturing => _isCapturing;
    public string WindowTitle { get; private set; } = "Garry's Mod";
    public (int Width, int Height) Resolution => (_width, _height);
    public float CurrentFps => _currentFps;

    public Task StartAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_isCapturing) return;

                // Find the game window
                _windowHandle = FindGameWindow();
                if (_windowHandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Window '{WindowTitle}' not found");
                }

                // Get window dimensions
                if (GetClientRect(_windowHandle, out var rect))
                {
                    _width = rect.Right - rect.Left;
                    _height = rect.Bottom - rect.Top;
                }
                else
                {
                    throw new InvalidOperationException("Could not get window dimensions");
                }

                _isCapturing = true;
                _frameId = 0;
            }
        }, ct);
    }

    public Task StopAsync()
    {
        lock (_lock)
        {
            _isCapturing = false;
            _windowHandle = IntPtr.Zero;
        }
        return Task.CompletedTask;
    }

    private IntPtr FindGameWindow()
    {
        IntPtr result = IntPtr.Zero;

        // Try exact title match first
        result = FindWindow(null, WindowTitle);
        if (result != IntPtr.Zero) return result;

        // Try partial match
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowTitle.Contains("Garry's Mod", StringComparison.OrdinalIgnoreCase) ||
                    process.ProcessName.Equals("hl2", StringComparison.OrdinalIgnoreCase) ||
                    process.ProcessName.Equals("gmod", StringComparison.OrdinalIgnoreCase))
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        return process.MainWindowHandle;
                    }
                }
            }
            catch
            {
                // Process may have exited
            }
        }

        return IntPtr.Zero;
    }

    public Frame? GetFrame()
    {
        if (!_isCapturing || _windowHandle == IntPtr.Zero)
            return null;

        lock (_lock)
        {
            try
            {
                // Check if window still exists
                if (!IsWindow(_windowHandle))
                {
                    _windowHandle = FindGameWindow();
                    if (_windowHandle == IntPtr.Zero)
                        return null;
                }

                // Get device contexts
                var hdcWindow = GetDC(_windowHandle);
                if (hdcWindow == IntPtr.Zero) return null;

                try
                {
                    var hdcMem = CreateCompatibleDC(hdcWindow);
                    if (hdcMem == IntPtr.Zero) return null;

                    try
                    {
                        var hBitmap = CreateCompatibleBitmap(hdcWindow, _width, _height);
                        if (hBitmap == IntPtr.Zero) return null;

                        try
                        {
                            var hOld = SelectObject(hdcMem, hBitmap);

                            // Capture window contents
                            BitBlt(hdcMem, 0, 0, _width, _height, hdcWindow, 0, 0, SRCCOPY);

                            SelectObject(hdcMem, hOld);

                            // Get bitmap data
                            var data = new byte[_width * _height * 4];
                            var bmi = new BITMAPINFO
                            {
                                biSize = 40,
                                biWidth = _width,
                                biHeight = -_height, // Top-down
                                biPlanes = 1,
                                biBitCount = 32,
                                biCompression = 0
                            };

                            GetDIBits(hdcWindow, hBitmap, 0, (uint)_height, data, ref bmi, 0);

                            var frame = new Frame(_frameId++, DateTime.UtcNow, _width, _height, data);
                            UpdateFpsCounter();
                            return frame;
                        }
                        finally
                        {
                            DeleteObject(hBitmap);
                        }
                    }
                    finally
                    {
                        DeleteDC(hdcMem);
                    }
                }
                finally
                {
                    ReleaseDC(_windowHandle, hdcWindow);
                }
            }
            catch
            {
                return null;
            }
        }
    }

    private void UpdateFpsCounter()
    {
        _frameCount++;
        var elapsed = (DateTime.UtcNow - _fpsCounterStart).TotalSeconds;
        if (elapsed >= 1.0)
        {
            _currentFps = (float)(_frameCount / elapsed);
            _frameCount = 0;
            _fpsCounterStart = DateTime.UtcNow;
        }
    }

    public void Dispose()
    {
        StopAsync().Wait();
    }

    #region Win32 Interop

    private const int SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSource, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    #endregion
}
