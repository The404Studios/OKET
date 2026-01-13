using OKET.Core.Types;

namespace OKET.Core.Policy;

/// <summary>
/// Represents the agent's current intent - what it's trying to accomplish.
/// Intent is higher-level than action; it's the "why" not the "what".
/// </summary>
public sealed class AgentIntent
{
    /// <summary>Type of intent.</summary>
    public IntentType Type { get; init; }

    /// <summary>Target position (if spatial).</summary>
    public Vector2? TargetPosition { get; init; }

    /// <summary>Target entity ID (if entity-directed).</summary>
    public int? TargetEntityId { get; init; }

    /// <summary>Confidence in this intent [0, 1].</summary>
    public float Confidence { get; init; }

    /// <summary>Priority relative to other possible intents.</summary>
    public float Priority { get; init; }

    /// <summary>Reason for this intent (human-readable).</summary>
    public string Reason { get; init; } = "";

    /// <summary>Frame when intent was formed.</summary>
    public long CreatedAtFrame { get; init; }

    /// <summary>How many frames this intent has been active.</summary>
    public int Age { get; set; }

    /// <summary>Whether this intent is still valid.</summary>
    public bool IsValid { get; set; } = true;

    /// <summary>Create an idle intent.</summary>
    public static AgentIntent Idle(long frame) => new()
    {
        Type = IntentType.Idle,
        Confidence = 1f,
        Priority = 0f,
        Reason = "No active goal",
        CreatedAtFrame = frame
    };

    /// <summary>Create a survival intent.</summary>
    public static AgentIntent Survive(float confidence, long frame) => new()
    {
        Type = IntentType.Survive,
        Confidence = confidence,
        Priority = 1f,
        Reason = "Stay alive",
        CreatedAtFrame = frame
    };

    /// <summary>Create an engage intent.</summary>
    public static AgentIntent Engage(int targetId, Vector2 position, float confidence, long frame) => new()
    {
        Type = IntentType.Engage,
        TargetEntityId = targetId,
        TargetPosition = position,
        Confidence = confidence,
        Priority = 0.8f,
        Reason = $"Engage target {targetId}",
        CreatedAtFrame = frame
    };

    /// <summary>Create a navigation intent.</summary>
    public static AgentIntent Navigate(Vector2 target, string reason, float confidence, long frame) => new()
    {
        Type = IntentType.Navigate,
        TargetPosition = target,
        Confidence = confidence,
        Priority = 0.5f,
        Reason = reason,
        CreatedAtFrame = frame
    };

    /// <summary>Create a retreat intent.</summary>
    public static AgentIntent Retreat(Vector2 safePosition, float confidence, long frame) => new()
    {
        Type = IntentType.Retreat,
        TargetPosition = safePosition,
        Confidence = confidence,
        Priority = 0.9f,
        Reason = "Retreat to safety",
        CreatedAtFrame = frame
    };

    public override string ToString() =>
        $"[{Type}] {Reason} (conf={Confidence:F2}, pri={Priority:F2})";
}

/// <summary>
/// Types of agent intent.
/// </summary>
public enum IntentType
{
    /// <summary>No active goal.</summary>
    Idle,

    /// <summary>Primary goal: stay alive.</summary>
    Survive,

    /// <summary>Engage a specific target.</summary>
    Engage,

    /// <summary>Move to a location.</summary>
    Navigate,

    /// <summary>Move away from danger.</summary>
    Retreat,

    /// <summary>Acquire resources (ammo, health, weapons).</summary>
    Acquire,

    /// <summary>Support teammates.</summary>
    Support,

    /// <summary>Hold position / defend.</summary>
    Defend,

    /// <summary>Explore unknown area.</summary>
    Explore
}
