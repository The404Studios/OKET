using System.Drawing;
using System.Runtime.InteropServices;

namespace OKET.Vision.Overlay;

/// <summary>
/// Transparent overlay window that sits on top of the game window.
/// Used for rendering debug visualizations without interfering with gameplay.
/// </summary>
public sealed class OverlayWindow : IDisposable
{
    private readonly IntPtr _targetWindow;
    private IntPtr _overlayHandle;
    private readonly int _width;
    private readonly int _height;
    private bool _disposed;
    private bool _isVisible;

    private readonly Thread? _renderThread;
    private volatile bool _running;
    private readonly DebugOverlay _debugOverlay;
    private Bitmap? _currentFrame;
    private readonly object _frameLock = new();

    public DebugOverlay DebugOverlay => _debugOverlay;
    public bool IsVisible => _isVisible;

    public OverlayWindow(IntPtr targetWindow, int width = 1920, int height = 1080)
    {
        _targetWindow = targetWindow;
        _width = width;
        _height = height;
        _debugOverlay = new DebugOverlay(targetWindow, width, height);

        // Create overlay window
        CreateOverlayWindow();

        // Start render thread
        _running = true;
        _renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "OverlayRenderThread"
        };
        _renderThread.Start();
    }

    private void CreateOverlayWindow()
    {
        // Register window class
        var wndClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate<WndProc>(WndProcHandler),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = GetModuleHandle(null),
            hCursor = LoadCursor(IntPtr.Zero, 32512), // IDC_ARROW
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = "OKETOverlay"
        };

        RegisterClassEx(ref wndClass);

        // Get target window position
        GetWindowRect(_targetWindow, out var targetRect);
        int x = targetRect.Left;
        int y = targetRect.Top;

        // Create overlay window
        _overlayHandle = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
            "OKETOverlay",
            "OKET AGI",
            WS_POPUP,
            x, y, _width, _height,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

        if (_overlayHandle == IntPtr.Zero)
        {
            throw new Exception($"Failed to create overlay window: {Marshal.GetLastWin32Error()}");
        }

        // Make window click-through
        SetLayeredWindowAttributes(_overlayHandle, 0, 255, LWA_ALPHA);
    }

    /// <summary>
    /// Show the overlay window.
    /// </summary>
    public void Show()
    {
        if (_overlayHandle != IntPtr.Zero)
        {
            ShowWindow(_overlayHandle, SW_SHOWNOACTIVATE);
            _isVisible = true;
        }
    }

    /// <summary>
    /// Hide the overlay window.
    /// </summary>
    public void Hide()
    {
        if (_overlayHandle != IntPtr.Zero)
        {
            ShowWindow(_overlayHandle, SW_HIDE);
            _isVisible = false;
        }
    }

    /// <summary>
    /// Toggle overlay visibility.
    /// </summary>
    public void Toggle()
    {
        if (_isVisible)
            Hide();
        else
            Show();
    }

    /// <summary>
    /// Update overlay position to match target window.
    /// </summary>
    public void UpdatePosition()
    {
        if (_overlayHandle == IntPtr.Zero || _targetWindow == IntPtr.Zero)
            return;

        GetWindowRect(_targetWindow, out var targetRect);
        SetWindowPos(_overlayHandle, HWND_TOPMOST,
            targetRect.Left, targetRect.Top,
            targetRect.Right - targetRect.Left,
            targetRect.Bottom - targetRect.Top,
            SWP_NOACTIVATE);
    }

    private void RenderLoop()
    {
        while (_running)
        {
            try
            {
                if (_isVisible && _overlayHandle != IntPtr.Zero)
                {
                    // Render debug overlay to bitmap
                    var newFrame = _debugOverlay.Render();

                    lock (_frameLock)
                    {
                        _currentFrame?.Dispose();
                        _currentFrame = newFrame;
                    }

                    // Update layered window
                    UpdateLayeredWindow();
                }

                Thread.Sleep(16); // ~60 FPS
            }
            catch
            {
                // Ignore render errors
            }
        }
    }

    private void UpdateLayeredWindow()
    {
        Bitmap frame;
        lock (_frameLock)
        {
            if (_currentFrame is null)
                return;
            frame = (Bitmap)_currentFrame.Clone();
        }

        try
        {
            var screenDC = GetDC(IntPtr.Zero);
            var memDC = CreateCompatibleDC(screenDC);
            var hBitmap = frame.GetHbitmap(Color.FromArgb(0));
            var oldBitmap = SelectObject(memDC, hBitmap);

            var size = new SIZE { cx = frame.Width, cy = frame.Height };
            var pointSource = new POINT { x = 0, y = 0 };

            GetWindowRect(_overlayHandle, out var windowRect);
            var topPos = new POINT { x = windowRect.Left, y = windowRect.Top };

            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };

            UpdateLayeredWindow(_overlayHandle, screenDC, ref topPos, ref size,
                memDC, ref pointSource, 0, ref blend, ULW_ALPHA);

            SelectObject(memDC, oldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(memDC);
            ReleaseDC(IntPtr.Zero, screenDC);
        }
        finally
        {
            frame.Dispose();
        }
    }

    private static IntPtr WndProcHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _running = false;
        _renderThread?.Join(1000);

        if (_overlayHandle != IntPtr.Zero)
        {
            DestroyWindow(_overlayHandle);
            _overlayHandle = IntPtr.Zero;
        }

        lock (_frameLock)
        {
            _currentFrame?.Dispose();
            _currentFrame = null;
        }

        _debugOverlay.Dispose();
    }

    #region Win32 Interop

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOPMOST = 0x8;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_HIDE = 0;
    private const int LWA_ALPHA = 0x2;
    private const int SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int ULW_ALPHA = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPStr)]
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll")]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    #endregion
}
