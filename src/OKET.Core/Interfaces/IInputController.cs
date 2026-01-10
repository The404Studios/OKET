using OKET.Core.Actions;

namespace OKET.Core.Interfaces;

/// <summary>
/// Controls keyboard and mouse input to the game.
/// </summary>
public interface IInputController : IDisposable
{
    /// <summary>Whether input is currently enabled.</summary>
    bool IsEnabled { get; set; }

    /// <summary>Execute an action plan.</summary>
    void Execute(ActionPlan plan);

    /// <summary>Execute a single action immediately.</summary>
    void Execute(GameAction action);

    /// <summary>Press a key (holds until released).</summary>
    void KeyDown(ActionType action);

    /// <summary>Release a key.</summary>
    void KeyUp(ActionType action);

    /// <summary>Tap a key (press and release).</summary>
    void KeyTap(ActionType action, int holdMs = 50);

    /// <summary>Move mouse by relative amount.</summary>
    void MouseMove(float dx, float dy);

    /// <summary>Move mouse toward a screen position with smoothing.</summary>
    void MouseMoveToward(float targetX, float targetY, float speed = 1f);

    /// <summary>Press mouse button.</summary>
    void MouseDown(int button = 0);

    /// <summary>Release mouse button.</summary>
    void MouseUp(int button = 0);

    /// <summary>Release all currently held keys.</summary>
    void ReleaseAll();

    /// <summary>Get currently held keys.</summary>
    IReadOnlySet<ActionType> HeldKeys { get; }
}
