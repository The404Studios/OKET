using OKET.Core.Types;
using OKET.Core.Navigation;

namespace OKET.Core.Policy;

/// <summary>
/// Navigation policy - decides WHEN and WHETHER to navigate, not just HOW.
/// Wraps navigation skill with decision logic.
/// </summary>
public sealed class NavigationPolicy
{
    /// <summary>Current navigation goal.</summary>
    public NavigationGoal? Goal { get; private set; }

    /// <summary>Confidence that current path is valid [0, 1].</summary>
    public float Confidence { get; private set; } = 1f;

    /// <summary>Risk level of current path [0, 1].</summary>
    public float Risk { get; private set; }

    /// <summary>Estimated cost (distance + turns + hazards).</summary>
    public float Cost { get; private set; }

    /// <summary>Current execution status.</summary>
    public PolicyStatus Status { get; private set; } = PolicyStatus.Idle;

    /// <summary>Frame when policy was last updated.</summary>
    public long LastUpdateFrame { get; private set; }

    /// <summary>Consecutive frames blocked.</summary>
    public int BlockedFrames { get; private set; }

    /// <summary>Whether navigation should execute this frame.</summary>
    public bool ShouldExecute => Status == PolicyStatus.Executing && Confidence > 0.3f;

    /// <summary>
    /// Set a new navigation goal.
    /// </summary>
    public void SetGoal(Vector2 target, string reason, float priority = 0.5f)
    {
        Goal = new NavigationGoal
        {
            Target = target,
            Reason = reason,
            Priority = priority,
            CreatedAtFrame = LastUpdateFrame
        };
        Status = PolicyStatus.Executing;
        Confidence = 1f;
        BlockedFrames = 0;
    }

    /// <summary>
    /// Set goal to follow an entity.
    /// </summary>
    public void SetFollowGoal(int entityId, Vector2 lastKnownPosition, float followDistance = 100f)
    {
        Goal = new NavigationGoal
        {
            Target = lastKnownPosition,
            TargetEntityId = entityId,
            FollowDistance = followDistance,
            Reason = $"Follow entity {entityId}",
            Priority = 0.6f,
            CreatedAtFrame = LastUpdateFrame
        };
        Status = PolicyStatus.Executing;
        Confidence = 0.8f; // Slightly lower confidence for moving targets
        BlockedFrames = 0;
    }

    /// <summary>
    /// Clear current goal.
    /// </summary>
    public void ClearGoal()
    {
        Goal = null;
        Status = PolicyStatus.Idle;
        Confidence = 1f;
        Risk = 0f;
        Cost = 0f;
    }

    /// <summary>
    /// Update policy based on current state.
    /// </summary>
    public void Update(long frame, Vector2 currentPosition, float distanceToGoal,
        int threatsOnPath, bool isBlocked, bool goalReached)
    {
        LastUpdateFrame = frame;

        if (Goal == null)
        {
            Status = PolicyStatus.Idle;
            return;
        }

        // Check completion
        if (goalReached || distanceToGoal < 20f)
        {
            Status = PolicyStatus.Complete;
            Goal = null;
            return;
        }

        // Update blocked state
        if (isBlocked)
        {
            BlockedFrames++;
            if (BlockedFrames > 30) // ~1 second at 30fps
            {
                Status = PolicyStatus.Blocked;
                Confidence *= 0.9f; // Decay confidence
            }
        }
        else
        {
            BlockedFrames = 0;
            if (Status == PolicyStatus.Blocked)
            {
                Status = PolicyStatus.Executing;
            }
        }

        // Update risk based on threats
        Risk = Math.Clamp(threatsOnPath * 0.2f, 0f, 1f);

        // Update cost
        Cost = distanceToGoal + (threatsOnPath * 50f);

        // Decay confidence over time if not making progress
        if (Status == PolicyStatus.Executing)
        {
            // Age the goal
            int goalAge = (int)(frame - Goal.CreatedAtFrame);
            if (goalAge > 300) // 10 seconds
            {
                Confidence *= 0.995f; // Slow decay
            }

            // If confidence drops too low, abandon
            if (Confidence < 0.2f)
            {
                Status = PolicyStatus.Abandoned;
            }
        }
    }

    /// <summary>
    /// Report that path was recalculated.
    /// </summary>
    public void OnPathRecalculated(bool success)
    {
        if (success)
        {
            Confidence = Math.Min(1f, Confidence + 0.1f);
            if (Status == PolicyStatus.Blocked)
            {
                Status = PolicyStatus.Executing;
            }
        }
        else
        {
            Confidence *= 0.7f;
        }
    }

    /// <summary>
    /// Report progress toward goal.
    /// </summary>
    public void OnProgress(float distanceMoved)
    {
        if (distanceMoved > 5f)
        {
            Confidence = Math.Min(1f, Confidence + 0.02f);
            BlockedFrames = 0;
        }
    }

    /// <summary>
    /// Get current state for debugging/overlay.
    /// </summary>
    public NavigationPolicyState GetState()
    {
        return new NavigationPolicyState
        {
            HasGoal = Goal != null,
            GoalPosition = Goal?.Target ?? default,
            GoalReason = Goal?.Reason ?? "",
            Confidence = Confidence,
            Risk = Risk,
            Cost = Cost,
            Status = Status,
            BlockedFrames = BlockedFrames
        };
    }
}

/// <summary>
/// Navigation goal definition.
/// </summary>
public sealed class NavigationGoal
{
    public Vector2 Target { get; init; }
    public int? TargetEntityId { get; init; }
    public float FollowDistance { get; init; }
    public string Reason { get; init; } = "";
    public float Priority { get; init; }
    public long CreatedAtFrame { get; init; }
}

/// <summary>
/// Snapshot of navigation policy state for debugging.
/// </summary>
public readonly struct NavigationPolicyState
{
    public bool HasGoal { get; init; }
    public Vector2 GoalPosition { get; init; }
    public string GoalReason { get; init; }
    public float Confidence { get; init; }
    public float Risk { get; init; }
    public float Cost { get; init; }
    public PolicyStatus Status { get; init; }
    public int BlockedFrames { get; init; }
}

/// <summary>
/// Policy execution status.
/// </summary>
public enum PolicyStatus
{
    /// <summary>No active goal.</summary>
    Idle,

    /// <summary>Actively executing toward goal.</summary>
    Executing,

    /// <summary>Blocked but still trying.</summary>
    Blocked,

    /// <summary>Goal reached successfully.</summary>
    Complete,

    /// <summary>Goal abandoned (confidence too low).</summary>
    Abandoned
}
