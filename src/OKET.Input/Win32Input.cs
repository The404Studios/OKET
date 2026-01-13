using System.Runtime.InteropServices;
using OKET.Core.Actions;
using OKET.Core.Interfaces;

namespace OKET.Input;

/// <summary>
/// Low-level Windows input using SendInput API.
/// Uses scan codes for game compatibility (Source Engine, etc.).
/// </summary>
public sealed class Win32Input : IInputController
{
    private readonly HashSet<ActionType> _heldKeys = new();
    private readonly Dictionary<ActionType, (ushort vk, ushort scan)> _keyMap;
    private bool _isEnabled = true;
    private IntPtr _targetWindow;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!value && _isEnabled)
            {
                ReleaseAll();
            }
            _isEnabled = value;
        }
    }

    public IReadOnlySet<ActionType> HeldKeys => _heldKeys;

    public Win32Input()
    {
        // Map action types to (virtual key code, scan code) pairs
        // Scan codes are hardware-level and work better with games
        _keyMap = new Dictionary<ActionType, (ushort vk, ushort scan)>
        {
            { ActionType.MoveForward, (0x57, 0x11) },    // W
            { ActionType.MoveBackward, (0x53, 0x1F) },   // S
            { ActionType.MoveLeft, (0x41, 0x1E) },       // A
            { ActionType.MoveRight, (0x44, 0x20) },      // D
            { ActionType.Jump, (0x20, 0x39) },           // Space
            { ActionType.Crouch, (0x11, 0x1D) },         // Ctrl
            { ActionType.Sprint, (0x10, 0x2A) },         // Shift
            { ActionType.Reload, (0x52, 0x13) },         // R
            { ActionType.Use, (0x45, 0x12) },            // E
            { ActionType.Weapon1, (0x31, 0x02) },        // 1
            { ActionType.Weapon2, (0x32, 0x03) },        // 2
            { ActionType.Weapon3, (0x33, 0x04) },        // 3
            { ActionType.Weapon4, (0x34, 0x05) },        // 4
            { ActionType.Flashlight, (0x46, 0x21) },     // F
        };
    }

    /// <summary>
    /// Set the target game window for input focus.
    /// </summary>
    public void SetTargetWindow(IntPtr hwnd)
    {
        _targetWindow = hwnd;
    }

    /// <summary>
    /// Ensure the game window is focused before sending input.
    /// </summary>
    public bool EnsureFocus()
    {
        if (_targetWindow == IntPtr.Zero)
        {
            _targetWindow = GetForegroundWindow();
        }

        var currentForeground = GetForegroundWindow();
        if (currentForeground != _targetWindow)
        {
            // Try to bring window to foreground
            SetForegroundWindow(_targetWindow);
            Thread.Sleep(10); // Small delay to ensure focus
            return GetForegroundWindow() == _targetWindow;
        }

        return true;
    }

    public void Execute(ActionPlan plan)
    {
        if (!_isEnabled) return;

        foreach (var action in plan.Actions)
        {
            Execute(action);
        }
    }

    public void Execute(GameAction action)
    {
        if (!_isEnabled) return;

        switch (action.Type)
        {
            case ActionType.MouseMove:
                MouseMove(action.MouseDelta.X, action.MouseDelta.Y);
                break;

            case ActionType.MouseMoveTo:
                MouseMoveTo(action.TargetPosition.X, action.TargetPosition.Y);
                break;

            case ActionType.Attack:
                if (action.IsPress)
                    MouseDown(0);
                else
                    MouseUp(0);
                break;

            case ActionType.AttackSecondary:
                if (action.IsPress)
                    MouseDown(1);
                else
                    MouseUp(1);
                break;

            case ActionType.StopAll:
                ReleaseAll();
                break;

            default:
                if (action.IsPress)
                    KeyDown(action.Type);
                else
                    KeyUp(action.Type);
                break;
        }
    }

    public void KeyDown(ActionType action)
    {
        if (!_isEnabled || !_keyMap.TryGetValue(action, out var key))
            return;

        if (_heldKeys.Contains(action))
            return; // Already held

        // Use scan code for game compatibility
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0, // Use 0 when using scan code
                    wScan = key.scan,
                    dwFlags = KEYEVENTF_SCANCODE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        _heldKeys.Add(action);
    }

    public void KeyUp(ActionType action)
    {
        if (!_keyMap.TryGetValue(action, out var key))
            return;

        if (!_heldKeys.Contains(action))
            return; // Not held

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = key.scan,
                    dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        _heldKeys.Remove(action);
    }

    public void KeyTap(ActionType action, int holdMs = 50)
    {
        KeyDown(action);
        Thread.Sleep(holdMs);
        KeyUp(action);
    }

    /// <summary>
    /// Press and release a key by virtual key code directly.
    /// </summary>
    public void DirectKeyTap(ushort vk, int holdMs = 50)
    {
        var scanCode = MapVirtualKey(vk, MAPVK_VK_TO_VSC);

        var downInput = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = (ushort)scanCode,
                    dwFlags = KEYEVENTF_SCANCODE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, new[] { downInput }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(holdMs);

        var upInput = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = (ushort)scanCode,
                    dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, new[] { upInput }, Marshal.SizeOf<INPUT>());
    }

    public void MouseMove(float dx, float dy)
    {
        if (!_isEnabled) return;

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)dx,
                    dy = (int)dy,
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_MOVE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public void MouseMoveTo(float targetX, float targetY)
    {
        if (!_isEnabled) return;

        // Get current cursor position
        if (!GetCursorPos(out var cursorPos))
            return;

        // Calculate delta
        float dx = targetX - cursorPos.X;
        float dy = targetY - cursorPos.Y;

        // Move in steps for smoother movement
        const int steps = 5;
        float stepX = dx / steps;
        float stepY = dy / steps;

        for (int i = 0; i < steps; i++)
        {
            MouseMove(stepX, stepY);
            Thread.Sleep(2);
        }
    }

    public void MouseMoveToward(float targetX, float targetY, float speed = 1f)
    {
        if (!_isEnabled) return;

        if (!GetCursorPos(out var cursorPos))
            return;

        float dx = targetX - cursorPos.X;
        float dy = targetY - cursorPos.Y;

        // Normalize and scale by speed
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        if (distance > 1f)
        {
            float moveX = (dx / distance) * speed;
            float moveY = (dy / distance) * speed;
            MouseMove(moveX, moveY);
        }
    }

    public void MouseDown(int button = 0)
    {
        if (!_isEnabled) return;

        uint flags = button == 0 ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_RIGHTDOWN;

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public void MouseUp(int button = 0)
    {
        uint flags = button == 0 ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_RIGHTUP;

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public void MouseClick(int button = 0, int holdMs = 50)
    {
        MouseDown(button);
        Thread.Sleep(holdMs);
        MouseUp(button);
    }

    public void ReleaseAll()
    {
        // Release all held keys
        foreach (var action in _heldKeys.ToList())
        {
            KeyUp(action);
        }

        // Release mouse buttons
        MouseUp(0);
        MouseUp(1);

        _heldKeys.Clear();
    }

    public void Dispose()
    {
        ReleaseAll();
    }

    #region Win32 Interop

    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    #endregion
}
