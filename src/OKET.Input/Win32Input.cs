using System.Runtime.InteropServices;
using OKET.Core.Actions;
using OKET.Core.Interfaces;

namespace OKET.Input;

/// <summary>
/// Low-level Windows input using SendInput API.
/// Handles keyboard and mouse input to the game.
/// </summary>
public sealed class Win32Input : IInputController
{
    private readonly HashSet<ActionType> _heldKeys = new();
    private readonly Dictionary<ActionType, ushort> _keyMap;
    private bool _isEnabled = true;

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
        // Map action types to virtual key codes
        _keyMap = new Dictionary<ActionType, ushort>
        {
            { ActionType.MoveForward, 0x57 },    // W
            { ActionType.MoveBackward, 0x53 },   // S
            { ActionType.MoveLeft, 0x41 },       // A
            { ActionType.MoveRight, 0x44 },      // D
            { ActionType.Jump, 0x20 },           // Space
            { ActionType.Crouch, 0x11 },         // Ctrl
            { ActionType.Sprint, 0x10 },         // Shift
            { ActionType.Reload, 0x52 },         // R
            { ActionType.Use, 0x45 },            // E
            { ActionType.Weapon1, 0x31 },        // 1
            { ActionType.Weapon2, 0x32 },        // 2
            { ActionType.Weapon3, 0x33 },        // 3
            { ActionType.Weapon4, 0x34 },        // 4
            { ActionType.Flashlight, 0x46 },     // F
        };
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
                // For absolute positioning, convert to relative movement
                // This would need current cursor position tracking
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
        if (!_isEnabled || !_keyMap.TryGetValue(action, out var vk))
            return;

        if (_heldKeys.Contains(action))
            return; // Already held

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = 0
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        _heldKeys.Add(action);
    }

    public void KeyUp(ActionType action)
    {
        if (!_keyMap.TryGetValue(action, out var vk))
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
                    wVk = vk,
                    dwFlags = KEYEVENTF_KEYUP
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
                    dwFlags = MOUSEEVENTF_MOVE
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public void MouseMoveToward(float targetX, float targetY, float speed = 1f)
    {
        // This would need cursor tracking to calculate delta
        // For now, this is a simplified implementation
        // In a full implementation, you'd track cursor position
        // and calculate the delta needed to reach target

        // Placeholder: move a small amount toward target
        // Real implementation needs GetCursorPos integration
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
                    dwFlags = flags
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
                    dwFlags = flags
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
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
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

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
