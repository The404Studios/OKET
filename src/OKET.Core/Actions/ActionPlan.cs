namespace OKET.Core.Actions;

/// <summary>
/// A plan of actions to execute over time.
/// Output from the decision layer, input to the actuation layer.
/// </summary>
public sealed record ActionPlan
{
    /// <summary>Frame ID this plan was created for.</summary>
    public long FrameId { get; init; }

    /// <summary>Current strategic mode.</summary>
    public StrategicMode Mode { get; init; }

    /// <summary>Actions to execute.</summary>
    public List<GameAction> Actions { get; init; } = [];

    /// <summary>How long this plan should remain valid (ms).</summary>
    public int ValidityMs { get; init; } = 100;

    /// <summary>Reason/explanation for logging.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Confidence in this plan [0, 1].</summary>
    public float Confidence { get; init; } = 1f;

    /// <summary>Whether this plan can be interrupted by higher priority plans.</summary>
    public bool Interruptible { get; init; } = true;

    public static ActionPlan Empty(long frameId) => new()
    {
        FrameId = frameId,
        Mode = StrategicMode.Idle,
        Actions = [],
        Reason = "No action needed"
    };

    /// <summary>
    /// Create a simple single-action plan.
    /// </summary>
    public static ActionPlan Single(long frameId, StrategicMode mode, GameAction action, string reason) => new()
    {
        FrameId = frameId,
        Mode = mode,
        Actions = [action],
        Reason = reason
    };
}
