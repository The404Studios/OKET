namespace OKET.Core.Agent;

/// <summary>
/// Intent types - WHAT the agent wants to achieve.
/// Intent never specifies HOW - that's the Policy's job.
/// </summary>
public enum IntentType : byte
{
    /// <summary>No active goal.</summary>
    Idle = 0,

    /// <summary>Stay alive - avoid damage, find cover.</summary>
    Survive = 1,

    /// <summary>Move to a specific location.</summary>
    ReachTarget = 2,

    /// <summary>Avoid a known threat.</summary>
    AvoidThreat = 3,

    /// <summary>Engage and eliminate a threat.</summary>
    EngageThreat = 4,

    /// <summary>Pick up an item (health, ammo, weapon).</summary>
    AcquireItem = 5,

    /// <summary>Explore unknown areas.</summary>
    Explore = 6,

    /// <summary>Support teammate.</summary>
    SupportAlly = 7,

    /// <summary>Hold position/defend area.</summary>
    Defend = 8
}

/// <summary>
/// Intent - high-level goal with priority and urgency.
/// </summary>
public readonly record struct Intent(
    IntentType Type,
    float Priority,       // 0..1, higher = more important
    float Urgency,        // 0..1, higher = more time-sensitive
    long CreatedTick,
    string Reason         // human-readable reason for this intent
)
{
    public static Intent None => new(IntentType.Idle, 0f, 0f, 0, "No intent");

    public static Intent Create(IntentType type, float priority, float urgency, long tick, string reason) =>
        new(type, Math.Clamp(priority, 0f, 1f), Math.Clamp(urgency, 0f, 1f), tick, reason);
}

/// <summary>
/// Policy execution status.
/// </summary>
public enum PolicyStatus : byte
{
    /// <summary>Policy is proposed but not yet executing.</summary>
    Proposed = 1,

    /// <summary>Policy is currently executing.</summary>
    Executing = 2,

    /// <summary>Policy is blocked (waiting for condition).</summary>
    Blocked = 3,

    /// <summary>Policy completed successfully.</summary>
    Completed = 4,

    /// <summary>Policy failed.</summary>
    Failed = 5
}

/// <summary>
/// Policy interface - HOW to achieve an intent.
/// Policies select skills and parameters, but never execute actions directly.
/// </summary>
public interface IAgentPolicy
{
    /// <summary>Human-readable policy name.</summary>
    string Name { get; }

    /// <summary>Current execution status.</summary>
    PolicyStatus Status { get; }

    /// <summary>Confidence in this policy [0..1].</summary>
    float Confidence { get; }

    /// <summary>Estimated cost (time, resources) [0..1].</summary>
    float EstimatedCost { get; }

    /// <summary>Estimated risk [0..1].</summary>
    float EstimatedRisk { get; }

    /// <summary>Active skill being executed.</summary>
    string ActiveSkill { get; }
}

/// <summary>
/// Action types - mechanical outputs.
/// Actions are stateless - they just produce input.
/// </summary>
public enum ActionType : byte
{
    /// <summary>Do nothing.</summary>
    Idle = 0,

    /// <summary>Move toward a direction. ParamA=angle, ParamB=speed.</summary>
    MoveToward = 1,

    /// <summary>Strafe left/right. ParamA=direction (-1 left, +1 right), ParamB=speed.</summary>
    Strafe = 2,

    /// <summary>Turn to face direction. ParamA=targetX, ParamB=targetY.</summary>
    TurnTo = 3,

    /// <summary>Fire weapon.</summary>
    Fire = 4,

    /// <summary>Reload weapon.</summary>
    Reload = 5,

    /// <summary>Interact with object.</summary>
    Interact = 6,

    /// <summary>Switch weapon. ParamA=weaponSlot.</summary>
    SwitchWeapon = 7,

    /// <summary>Jump.</summary>
    Jump = 8,

    /// <summary>Crouch.</summary>
    Crouch = 9,

    /// <summary>Sprint.</summary>
    Sprint = 10
}

/// <summary>
/// Action plan - complete decision snapshot.
/// This is what the overlay displays and what gets logged.
/// </summary>
public readonly record struct ActionPlan(
    long TickId,
    Intent Intent,
    string PolicyName,
    ActionType Action,
    float ParamA,
    float ParamB,
    float Confidence
)
{
    public static ActionPlan Idle(long tick) => new(
        tick,
        Intent.None,
        "None",
        ActionType.Idle,
        0f, 0f, 0f
    );

    /// <summary>Human-readable action description.</summary>
    public string ActionDescription => Action switch
    {
        ActionType.Idle => "Idle",
        ActionType.MoveToward => $"Move {ParamA:F0}° @ {ParamB:P0}",
        ActionType.Strafe => ParamA < 0 ? "Strafe Left" : "Strafe Right",
        ActionType.TurnTo => $"Turn to ({ParamA:F0}, {ParamB:F0})",
        ActionType.Fire => "Fire",
        ActionType.Reload => "Reload",
        ActionType.Interact => "Interact",
        ActionType.SwitchWeapon => $"Switch to slot {ParamA:F0}",
        ActionType.Jump => "Jump",
        ActionType.Crouch => "Crouch",
        ActionType.Sprint => "Sprint",
        _ => Action.ToString()
    };
}

/// <summary>
/// Action outcome - result of executing an action.
/// Used for learning and debugging.
/// </summary>
public readonly record struct ActionOutcome(
    long TickId,
    ActionType Action,
    bool Success,
    float Reward,
    string? FailureReason
)
{
    public static ActionOutcome FromReward(long tick, ActionType action, float reward) =>
        new(tick, action, reward >= 0, reward, reward < 0 ? "Negative reward" : null);
}

/// <summary>
/// Complete agent state for display/logging.
/// </summary>
public sealed class AgentStateSnapshot
{
    public long TickId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    // Intent layer
    public IntentType Intent { get; init; }
    public string IntentReason { get; init; } = "";
    public float IntentPriority { get; init; }
    public float IntentUrgency { get; init; }

    // Policy layer
    public string PolicyName { get; init; } = "None";
    public PolicyStatus PolicyStatus { get; init; }
    public string ActiveSkill { get; init; } = "None";
    public float PolicyConfidence { get; init; }

    // Action layer
    public ActionType Action { get; init; }
    public string ActionDescription { get; init; } = "Idle";
    public float ActionConfidence { get; init; }

    // Feedback
    public float LastReward { get; init; }
    public float PredictionError { get; init; }
    public int ThreatCount { get; init; }
    public int Health { get; init; }
    public float Fps { get; init; }

    /// <summary>Create from action plan and game state.</summary>
    public static AgentStateSnapshot FromPlan(ActionPlan plan, float reward, float predError, int threats, int health, float fps) => new()
    {
        TickId = plan.TickId,
        Intent = plan.Intent.Type,
        IntentReason = plan.Intent.Reason,
        IntentPriority = plan.Intent.Priority,
        IntentUrgency = plan.Intent.Urgency,
        PolicyName = plan.PolicyName,
        ActiveSkill = plan.PolicyName,
        PolicyConfidence = plan.Confidence,
        Action = plan.Action,
        ActionDescription = plan.ActionDescription,
        ActionConfidence = plan.Confidence,
        LastReward = reward,
        PredictionError = predError,
        ThreatCount = threats,
        Health = health,
        Fps = fps
    };
}
