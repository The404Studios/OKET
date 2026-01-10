using OKET.Core.Types;

namespace OKET.Core.Actions;

/// <summary>
/// A single action to be executed.
/// </summary>
public sealed class GameAction
{
    /// <summary>Type of action.</summary>
    public ActionType Type { get; init; }

    /// <summary>Whether to press (true) or release (false) the key.</summary>
    public bool IsPress { get; init; } = true;

    /// <summary>Duration to hold the key (ms). 0 = tap.</summary>
    public int DurationMs { get; init; }

    /// <summary>For MouseMove: relative movement delta.</summary>
    public Vector2 MouseDelta { get; init; }

    /// <summary>For MouseMoveTo: target screen position.</summary>
    public Vector2 TargetPosition { get; init; }

    /// <summary>Priority for this action (higher = execute first).</summary>
    public int Priority { get; init; }

    /// <summary>
    /// Create a key press action.
    /// </summary>
    public static GameAction Press(ActionType type, int durationMs = 0) => new()
    {
        Type = type,
        IsPress = true,
        DurationMs = durationMs
    };

    /// <summary>
    /// Create a key release action.
    /// </summary>
    public static GameAction Release(ActionType type) => new()
    {
        Type = type,
        IsPress = false
    };

    /// <summary>
    /// Create a relative mouse move action.
    /// </summary>
    public static GameAction MouseMove(float dx, float dy) => new()
    {
        Type = ActionType.MouseMove,
        MouseDelta = new Vector2(dx, dy)
    };

    /// <summary>
    /// Create a mouse move toward target action.
    /// </summary>
    public static GameAction MouseMoveTo(Vector2 target) => new()
    {
        Type = ActionType.MouseMoveTo,
        TargetPosition = target
    };
}
