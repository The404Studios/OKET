using OKET.Core.Types;
using OKET.Core.Prediction;

namespace OKET.Core.Feedback;

/// <summary>
/// LOCK 3: Prediction Error as a First-Class Behavioral Signal
///
/// This system makes prediction error drive behavior changes:
/// - High error → explore or slow down
/// - Low error → exploit and refine
/// - Sustained error → downgrade skill confidence
///
/// This is how intelligence self-corrects instead of doubling down.
/// </summary>
public sealed class PredictionErrorSignal
{
    private readonly int _windowSize;
    private readonly Queue<ErrorSample> _history = new();
    private readonly Dictionary<string, SkillErrorTracker> _skillErrors = new();

    /// <summary>Current prediction error (exponential moving average).</summary>
    public float CurrentError { get; private set; }

    /// <summary>Error trend: positive = getting worse, negative = improving.</summary>
    public float ErrorTrend { get; private set; }

    /// <summary>Recommended behavior modifier based on error state.</summary>
    public BehaviorModifier Modifier { get; private set; } = new();

    /// <summary>Whether the system is in high-error state.</summary>
    public bool IsHighError => CurrentError > 50f;

    /// <summary>Whether predictions are currently reliable.</summary>
    public bool IsPredictionReliable => CurrentError < 30f && ErrorTrend <= 0;

    /// <summary>Frames since last low-error state.</summary>
    public int FramesSinceReliable { get; private set; }

    public PredictionErrorSignal(int windowSize = 100)
    {
        _windowSize = windowSize;
    }

    /// <summary>
    /// Record prediction error for the current frame.
    /// </summary>
    public void RecordError(float error, long frameId, string? activeSkill = null)
    {
        var sample = new ErrorSample
        {
            Error = error,
            FrameId = frameId,
            Timestamp = DateTime.UtcNow,
            ActiveSkill = activeSkill
        };

        _history.Enqueue(sample);
        while (_history.Count > _windowSize)
        {
            _history.Dequeue();
        }

        // Update EMA
        const float alpha = 0.1f;
        CurrentError = CurrentError * (1f - alpha) + error * alpha;

        // Update trend
        UpdateTrend();

        // Track per-skill errors
        if (!string.IsNullOrEmpty(activeSkill))
        {
            if (!_skillErrors.TryGetValue(activeSkill, out var tracker))
            {
                tracker = new SkillErrorTracker(activeSkill);
                _skillErrors[activeSkill] = tracker;
            }
            tracker.RecordError(error, frameId);
        }

        // Update frames since reliable
        if (IsPredictionReliable)
        {
            FramesSinceReliable = 0;
        }
        else
        {
            FramesSinceReliable++;
        }

        // Update behavior modifier
        UpdateModifier();
    }

    /// <summary>
    /// Get confidence modifier for a specific skill based on its error history.
    /// </summary>
    public float GetSkillConfidenceModifier(string skillName)
    {
        if (!_skillErrors.TryGetValue(skillName, out var tracker))
        {
            return 1f; // No data = default confidence
        }

        return tracker.ConfidenceModifier;
    }

    /// <summary>
    /// Get the current behavior recommendation based on error state.
    /// </summary>
    public BehaviorRecommendation GetRecommendation()
    {
        if (CurrentError > 80f || FramesSinceReliable > 60)
        {
            return BehaviorRecommendation.Explore;
        }

        if (CurrentError > 50f || ErrorTrend > 0.5f)
        {
            return BehaviorRecommendation.SlowDown;
        }

        if (CurrentError < 20f && ErrorTrend < 0)
        {
            return BehaviorRecommendation.Exploit;
        }

        return BehaviorRecommendation.Normal;
    }

    /// <summary>
    /// Check if a skill should be abandoned due to sustained errors.
    /// </summary>
    public bool ShouldAbandonSkill(string skillName, float errorThreshold = 60f, int frameThreshold = 90)
    {
        if (!_skillErrors.TryGetValue(skillName, out var tracker))
        {
            return false;
        }

        return tracker.AverageError > errorThreshold &&
               tracker.FramesSinceGood > frameThreshold;
    }

    /// <summary>
    /// Reset error tracking for a skill (e.g., when skill is restarted).
    /// </summary>
    public void ResetSkill(string skillName)
    {
        _skillErrors.Remove(skillName);
    }

    /// <summary>
    /// Get summary for debugging/display.
    /// </summary>
    public ErrorSummary GetSummary()
    {
        return new ErrorSummary
        {
            CurrentError = CurrentError,
            ErrorTrend = ErrorTrend,
            IsHighError = IsHighError,
            IsPredictionReliable = IsPredictionReliable,
            FramesSinceReliable = FramesSinceReliable,
            Recommendation = GetRecommendation(),
            Modifier = Modifier,
            SkillErrors = _skillErrors.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.AverageError)
        };
    }

    private void UpdateTrend()
    {
        if (_history.Count < 20)
        {
            ErrorTrend = 0;
            return;
        }

        // Compare recent errors to older errors
        var recent = _history.TakeLast(10).Average(s => s.Error);
        var older = _history.Skip(Math.Max(0, _history.Count - 20))
            .Take(10).Average(s => s.Error);

        ErrorTrend = (recent - older) / 10f; // Normalized rate of change
    }

    private void UpdateModifier()
    {
        var recommendation = GetRecommendation();

        Modifier = recommendation switch
        {
            BehaviorRecommendation.Explore => new BehaviorModifier
            {
                SpeedMultiplier = 0.5f,
                AggressionMultiplier = 0.3f,
                CautionMultiplier = 1.5f,
                ExplorationBias = 0.8f,
                ShouldInterrupt = FramesSinceReliable > 90
            },
            BehaviorRecommendation.SlowDown => new BehaviorModifier
            {
                SpeedMultiplier = 0.7f,
                AggressionMultiplier = 0.5f,
                CautionMultiplier = 1.3f,
                ExplorationBias = 0.3f,
                ShouldInterrupt = false
            },
            BehaviorRecommendation.Exploit => new BehaviorModifier
            {
                SpeedMultiplier = 1.2f,
                AggressionMultiplier = 1.1f,
                CautionMultiplier = 0.8f,
                ExplorationBias = 0.1f,
                ShouldInterrupt = false
            },
            _ => new BehaviorModifier
            {
                SpeedMultiplier = 1f,
                AggressionMultiplier = 1f,
                CautionMultiplier = 1f,
                ExplorationBias = 0.2f,
                ShouldInterrupt = false
            }
        };
    }
}

/// <summary>
/// Single error sample.
/// </summary>
internal readonly struct ErrorSample
{
    public float Error { get; init; }
    public long FrameId { get; init; }
    public DateTime Timestamp { get; init; }
    public string? ActiveSkill { get; init; }
}

/// <summary>
/// Tracks error for a specific skill.
/// </summary>
internal sealed class SkillErrorTracker
{
    public string SkillName { get; }
    public float AverageError { get; private set; }
    public float ConfidenceModifier { get; private set; } = 1f;
    public int FramesSinceGood { get; private set; }
    public int TotalFrames { get; private set; }

    private readonly Queue<float> _recentErrors = new();
    private const int WindowSize = 30;

    public SkillErrorTracker(string skillName)
    {
        SkillName = skillName;
    }

    public void RecordError(float error, long frameId)
    {
        _recentErrors.Enqueue(error);
        while (_recentErrors.Count > WindowSize)
        {
            _recentErrors.Dequeue();
        }

        AverageError = _recentErrors.Average();
        TotalFrames++;

        // Update frames since good
        if (error < 30f)
        {
            FramesSinceGood = 0;
        }
        else
        {
            FramesSinceGood++;
        }

        // Update confidence modifier
        ConfidenceModifier = CalculateConfidenceModifier();
    }

    private float CalculateConfidenceModifier()
    {
        // High average error = low confidence
        float errorFactor = Math.Clamp(1f - (AverageError / 100f), 0.3f, 1f);

        // Sustained errors = lower confidence
        float sustainedFactor = FramesSinceGood > 60
            ? 0.5f
            : FramesSinceGood > 30
                ? 0.7f
                : 1f;

        return errorFactor * sustainedFactor;
    }
}

/// <summary>
/// Behavior recommendation based on error state.
/// </summary>
public enum BehaviorRecommendation
{
    /// <summary>Normal operation.</summary>
    Normal,

    /// <summary>Slow down and be more careful.</summary>
    SlowDown,

    /// <summary>Predictions are working - exploit them.</summary>
    Exploit,

    /// <summary>High error - explore/try different approaches.</summary>
    Explore
}

/// <summary>
/// Modifiers to apply to behavior based on error state.
/// </summary>
public sealed class BehaviorModifier
{
    /// <summary>Multiply movement/action speed by this.</summary>
    public float SpeedMultiplier { get; init; } = 1f;

    /// <summary>Multiply aggression by this.</summary>
    public float AggressionMultiplier { get; init; } = 1f;

    /// <summary>Multiply caution by this.</summary>
    public float CautionMultiplier { get; init; } = 1f;

    /// <summary>Bias toward exploration vs exploitation [0, 1].</summary>
    public float ExplorationBias { get; init; } = 0.2f;

    /// <summary>Whether current action should be interrupted.</summary>
    public bool ShouldInterrupt { get; init; }
}

/// <summary>
/// Summary of error state for debugging.
/// </summary>
public sealed class ErrorSummary
{
    public float CurrentError { get; init; }
    public float ErrorTrend { get; init; }
    public bool IsHighError { get; init; }
    public bool IsPredictionReliable { get; init; }
    public int FramesSinceReliable { get; init; }
    public BehaviorRecommendation Recommendation { get; init; }
    public BehaviorModifier Modifier { get; init; } = new();
    public Dictionary<string, float> SkillErrors { get; init; } = new();
}
