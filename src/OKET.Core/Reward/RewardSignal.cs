using OKET.Core.Types;

namespace OKET.Core.Reward;

/// <summary>
/// Reward signal attached to action results.
/// Strict, minimal set - do NOT expand without justification.
/// </summary>
public readonly struct RewardSignal
{
    /// <summary>Type of reward.</summary>
    public RewardType Type { get; init; }

    /// <summary>Reward value (can be negative for penalties).</summary>
    public float Value { get; init; }

    /// <summary>Frame when reward was generated.</summary>
    public long Frame { get; init; }

    /// <summary>Optional context.</summary>
    public string Context { get; init; }

    public override string ToString() =>
        $"{Type}: {Value:+0.00;-0.00} @ frame {Frame}";
}

/// <summary>
/// Types of reward signals.
/// STRICT: Maximum 5 types to prevent reward hacking.
/// </summary>
public enum RewardType
{
    /// <summary>+1.0: Stayed alive this tick.</summary>
    Survival,

    /// <summary>+0.5: Made progress toward goal.</summary>
    Progress,

    /// <summary>-1.0: Took damage.</summary>
    Damage,

    /// <summary>-0.2: Idle without valid reason.</summary>
    Idle,

    /// <summary>-0.5: Movement blocked.</summary>
    Blocked
}

/// <summary>
/// Calculates reward signals from state transitions.
/// </summary>
public sealed class RewardCalculator
{
    private int _lastHealth = 100;
    private long _lastProgressFrame;
    private Vector2 _lastPosition;
    private int _idleFrames;
    private int _blockedFrames;

    /// <summary>
    /// Calculate rewards for the current frame.
    /// </summary>
    public RewardResult Calculate(
        long frame,
        int health,
        Vector2 position,
        float distanceToGoal,
        float lastDistanceToGoal,
        bool hasGoal,
        bool isBlocked)
    {
        var result = new RewardResult { Frame = frame };

        // 1. Survival: +1 for staying alive
        result.Signals.Add(new RewardSignal
        {
            Type = RewardType.Survival,
            Value = 1.0f,
            Frame = frame,
            Context = "Alive"
        });
        result.TotalReward += 1.0f;

        // 2. Progress: +0.5 for moving closer to goal
        if (hasGoal && lastDistanceToGoal > 0)
        {
            float progressDelta = lastDistanceToGoal - distanceToGoal;
            if (progressDelta > 5f) // Meaningful progress
            {
                result.Signals.Add(new RewardSignal
                {
                    Type = RewardType.Progress,
                    Value = 0.5f,
                    Frame = frame,
                    Context = $"Moved {progressDelta:F0}px closer"
                });
                result.TotalReward += 0.5f;
                _lastProgressFrame = frame;
                _idleFrames = 0;
            }
        }

        // 3. Damage: -1 per damage tick
        if (health < _lastHealth)
        {
            int damageTaken = _lastHealth - health;
            float penalty = -1.0f * (damageTaken / 10f); // Scale by damage amount
            penalty = Math.Max(penalty, -5f); // Cap penalty

            result.Signals.Add(new RewardSignal
            {
                Type = RewardType.Damage,
                Value = penalty,
                Frame = frame,
                Context = $"Took {damageTaken} damage"
            });
            result.TotalReward += penalty;
        }
        _lastHealth = health;

        // 4. Idle: -0.2 for being idle without reason
        if (!hasGoal && Vector2.Distance(position, _lastPosition) < 2f)
        {
            _idleFrames++;
            if (_idleFrames > 30) // 1 second of idleness
            {
                result.Signals.Add(new RewardSignal
                {
                    Type = RewardType.Idle,
                    Value = -0.2f,
                    Frame = frame,
                    Context = $"Idle for {_idleFrames} frames"
                });
                result.TotalReward -= 0.2f;
            }
        }
        else
        {
            _idleFrames = 0;
        }

        // 5. Blocked: -0.5 for blocked movement
        if (isBlocked)
        {
            _blockedFrames++;
            if (_blockedFrames > 10)
            {
                result.Signals.Add(new RewardSignal
                {
                    Type = RewardType.Blocked,
                    Value = -0.5f,
                    Frame = frame,
                    Context = $"Blocked for {_blockedFrames} frames"
                });
                result.TotalReward -= 0.5f;
            }
        }
        else
        {
            _blockedFrames = 0;
        }

        _lastPosition = position;
        return result;
    }

    /// <summary>
    /// Reset calculator state (e.g., on death).
    /// </summary>
    public void Reset()
    {
        _lastHealth = 100;
        _lastProgressFrame = 0;
        _lastPosition = Vector2.Zero;
        _idleFrames = 0;
        _blockedFrames = 0;
    }
}

/// <summary>
/// Result of reward calculation for a frame.
/// </summary>
public sealed class RewardResult
{
    public long Frame { get; init; }
    public List<RewardSignal> Signals { get; } = new();
    public float TotalReward { get; set; }

    /// <summary>
    /// Get dominant signal (highest absolute value).
    /// </summary>
    public RewardSignal? DominantSignal =>
        Signals.OrderByDescending(s => Math.Abs(s.Value)).FirstOrDefault();
}
