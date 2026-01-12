namespace OKET.Core.Gradients;

/// <summary>
/// Memory Layer: Store transitions, not screenshots.
///
/// PRINCIPLE: Memory is CAUSAL, not visual.
///
/// Each experience record contains:
/// - Superstate_before: What was the situation?
/// - Action_taken: What did we do?
/// - Superstate_after: What happened?
/// - Outcome: Was it good or bad?
/// - Novelty: How new was this?
///
/// This enables:
/// - Learning from experience (what works in which situations)
/// - Recognizing similar situations (even if never seen exactly)
/// - Training → Learning → Training cycle
/// </summary>
public sealed class TransitionMemory
{
    private readonly List<TransitionRecord> _records = new();
    private readonly Dictionary<int, List<int>> _byPatternId = new(); // Pattern → record indices
    private readonly Dictionary<ActionType, ActionStats> _actionStats = new();

    private int _nextRecordId;
    private readonly int _maxRecords;

    // Statistics
    private int _totalTransitions;
    private int _positiveOutcomes;
    private int _negativeOutcomes;
    private float _avgNovelty;

    public int RecordCount => _records.Count;
    public int TotalTransitions => _totalTransitions;
    public float PositiveRate => _totalTransitions > 0 ? (float)_positiveOutcomes / _totalTransitions : 0.5f;
    public float AverageNovelty => _avgNovelty;

    public TransitionMemory(int maxRecords = 10000)
    {
        _maxRecords = maxRecords;
    }

    /// <summary>
    /// Record a transition (before → action → after → outcome).
    /// </summary>
    public int RecordTransition(
        SuperstateSignature before,
        ActionType action,
        SuperstateSignature after,
        TransitionOutcome outcome,
        float novelty)
    {
        int recordId = _nextRecordId++;
        _totalTransitions++;

        var record = new TransitionRecord
        {
            RecordId = recordId,
            Timestamp = DateTime.UtcNow,
            Before = before,
            Action = action,
            After = after,
            Outcome = outcome,
            Novelty = novelty
        };

        // Add to main list
        _records.Add(record);

        // Index by pattern if recognized
        if (before.Type != SuperstateType.Unknown)
        {
            int patternKey = (int)before.Type;
            if (!_byPatternId.ContainsKey(patternKey))
                _byPatternId[patternKey] = new List<int>();
            _byPatternId[patternKey].Add(recordId);
        }

        // Update action stats
        UpdateActionStats(action, outcome);

        // Update outcome counters
        if (outcome.Success > 0.5f)
            _positiveOutcomes++;
        else if (outcome.Success < -0.5f)
            _negativeOutcomes++;

        // Update novelty average
        _avgNovelty = _avgNovelty * 0.99f + novelty * 0.01f;

        // Prune old records if needed
        if (_records.Count > _maxRecords)
        {
            PruneOldRecords();
        }

        return recordId;
    }

    private void UpdateActionStats(ActionType action, TransitionOutcome outcome)
    {
        if (!_actionStats.TryGetValue(action, out var stats))
        {
            stats = new ActionStats();
            _actionStats[action] = stats;
        }

        stats.TotalAttempts++;
        stats.SuccessSum += outcome.Success;
        stats.RiskSum += outcome.Risk;
        stats.InfoGainSum += outcome.InfoGain;

        if (outcome.Success > 0.5f) stats.Successes++;
        if (outcome.Success < -0.5f) stats.Failures++;
    }

    /// <summary>
    /// Query transitions similar to a given situation.
    /// </summary>
    public IEnumerable<TransitionRecord> QuerySimilar(SuperstateSignature situation, int maxResults = 10)
    {
        return _records
            .Select(r => (record: r, similarity: r.Before.SimilarityTo(situation)))
            .Where(x => x.similarity > 0.5f)
            .OrderByDescending(x => x.similarity)
            .Take(maxResults)
            .Select(x => x.record);
    }

    /// <summary>
    /// Query transitions for a specific action.
    /// </summary>
    public IEnumerable<TransitionRecord> QueryByAction(ActionType action, int maxResults = 20)
    {
        return _records
            .Where(r => r.Action == action)
            .OrderByDescending(r => r.Timestamp)
            .Take(maxResults);
    }

    /// <summary>
    /// Query transitions by pattern type.
    /// </summary>
    public IEnumerable<TransitionRecord> QueryByPattern(SuperstateType type, int maxResults = 20)
    {
        int patternKey = (int)type;
        if (!_byPatternId.TryGetValue(patternKey, out var indices))
            return Enumerable.Empty<TransitionRecord>();

        return indices
            .TakeLast(maxResults)
            .Select(i => _records.FirstOrDefault(r => r.RecordId == i))
            .Where(r => r.RecordId >= 0);
    }

    /// <summary>
    /// Get expected outcome for an action in a situation.
    /// </summary>
    public ExpectedOutcome GetExpectedOutcome(SuperstateSignature situation, ActionType action)
    {
        // Find similar past situations where this action was taken
        var similar = _records
            .Where(r => r.Action == action)
            .Select(r => (record: r, similarity: r.Before.SimilarityTo(situation)))
            .Where(x => x.similarity > 0.4f)
            .ToList();

        if (similar.Count == 0)
        {
            // No data - return uncertain
            return new ExpectedOutcome
            {
                Success = 0,
                Risk = 0.5f,
                InfoGain = 1f, // High info gain because we don't know
                Confidence = 0,
                SampleCount = 0
            };
        }

        // Weighted average by similarity
        float totalWeight = similar.Sum(x => x.similarity);
        float avgSuccess = similar.Sum(x => x.record.Outcome.Success * x.similarity) / totalWeight;
        float avgRisk = similar.Sum(x => x.record.Outcome.Risk * x.similarity) / totalWeight;
        float avgInfoGain = similar.Sum(x => x.record.Outcome.InfoGain * x.similarity) / totalWeight;

        // Confidence based on sample count and similarity
        float confidence = Math.Min(1f, similar.Count / 10f) *
                          similar.Average(x => x.similarity);

        return new ExpectedOutcome
        {
            Success = avgSuccess,
            Risk = avgRisk,
            InfoGain = avgInfoGain,
            Confidence = confidence,
            SampleCount = similar.Count
        };
    }

    /// <summary>
    /// Get action statistics.
    /// </summary>
    public ActionStats GetActionStats(ActionType action)
    {
        return _actionStats.GetValueOrDefault(action, new ActionStats());
    }

    /// <summary>
    /// Get best action for a situation based on historical data.
    /// </summary>
    public (ActionType action, float score, float confidence) GetBestAction(SuperstateSignature situation)
    {
        ActionType bestAction = ActionType.Observe;
        float bestScore = float.MinValue;
        float bestConfidence = 0;

        foreach (ActionType action in Enum.GetValues<ActionType>())
        {
            var expected = GetExpectedOutcome(situation, action);

            // Score = expected success - risk + info_gain_bonus (for exploration)
            float explorationBonus = expected.Confidence < 0.3f ? 0.2f * expected.InfoGain : 0;
            float score = expected.Success - expected.Risk * 0.5f + explorationBonus;

            if (score > bestScore)
            {
                bestScore = score;
                bestAction = action;
                bestConfidence = expected.Confidence;
            }
        }

        return (bestAction, bestScore, bestConfidence);
    }

    private void PruneOldRecords()
    {
        // Remove oldest 10%
        int removeCount = _maxRecords / 10;
        var toRemove = _records.Take(removeCount).ToList();

        foreach (var record in toRemove)
        {
            _records.Remove(record);
        }

        // Rebuild index
        _byPatternId.Clear();
        foreach (var record in _records)
        {
            int patternKey = (int)record.Before.Type;
            if (!_byPatternId.ContainsKey(patternKey))
                _byPatternId[patternKey] = new List<int>();
            _byPatternId[patternKey].Add(record.RecordId);
        }
    }

    /// <summary>
    /// Get diagnostics.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"""
            === TRANSITION MEMORY ===
            Records: {_records.Count} / {_maxRecords}
            Total Transitions: {_totalTransitions}
            Positive: {_positiveOutcomes} ({PositiveRate:P0})
            Negative: {_negativeOutcomes}
            Avg Novelty: {_avgNovelty:F2}

            Action Stats:
            {string.Join("\n", _actionStats.Select(kv => $"  {kv.Key}: {kv.Value.SuccessRate:P0} ({kv.Value.TotalAttempts})"))}
            =========================
            """;
    }
}

/// <summary>
/// A single transition record.
/// </summary>
public readonly struct TransitionRecord
{
    public int RecordId { get; init; }
    public DateTime Timestamp { get; init; }
    public SuperstateSignature Before { get; init; }
    public ActionType Action { get; init; }
    public SuperstateSignature After { get; init; }
    public TransitionOutcome Outcome { get; init; }
    public float Novelty { get; init; }
}

/// <summary>
/// Outcome of a transition.
/// </summary>
public readonly struct TransitionOutcome
{
    /// <summary>Success/reward value [-1, 1].</summary>
    public float Success { get; init; }

    /// <summary>Risk incurred [0, 1].</summary>
    public float Risk { get; init; }

    /// <summary>Information gained (did we learn something?) [0, 1].</summary>
    public float InfoGain { get; init; }

    /// <summary>Did situation improve?</summary>
    public bool Improved { get; init; }

    /// <summary>Did we survive?</summary>
    public bool Survived { get; init; }
}

/// <summary>
/// Expected outcome prediction.
/// </summary>
public readonly struct ExpectedOutcome
{
    public float Success { get; init; }
    public float Risk { get; init; }
    public float InfoGain { get; init; }
    public float Confidence { get; init; }
    public int SampleCount { get; init; }
}

/// <summary>
/// Statistics for an action type.
/// </summary>
public sealed class ActionStats
{
    public int TotalAttempts { get; set; }
    public int Successes { get; set; }
    public int Failures { get; set; }
    public float SuccessSum { get; set; }
    public float RiskSum { get; set; }
    public float InfoGainSum { get; set; }

    public float SuccessRate => TotalAttempts > 0 ? (float)Successes / TotalAttempts : 0.5f;
    public float AverageSuccess => TotalAttempts > 0 ? SuccessSum / TotalAttempts : 0;
    public float AverageRisk => TotalAttempts > 0 ? RiskSum / TotalAttempts : 0.5f;
}

/// <summary>
/// High-level action types for memory.
/// </summary>
public enum ActionType
{
    /// <summary>Just observe, no action.</summary>
    Observe,

    /// <summary>Engage/attack.</summary>
    Engage,

    /// <summary>Move toward something.</summary>
    Approach,

    /// <summary>Move away/retreat.</summary>
    Retreat,

    /// <summary>Strafe/kite while engaging.</summary>
    Kite,

    /// <summary>Interact with object.</summary>
    Interact,

    /// <summary>Wait/pause.</summary>
    Wait,

    /// <summary>Explore (move to unknown area).</summary>
    Explore
}
