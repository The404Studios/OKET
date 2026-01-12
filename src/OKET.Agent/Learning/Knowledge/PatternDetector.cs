namespace OKET.Agent.Learning.Knowledge;

/// <summary>
/// Detects potential laws, rules, and patterns from experience data.
/// Uses statistical analysis to discover regularities in state-action-outcome sequences.
/// </summary>
public sealed class PatternDetector
{
    private readonly List<TransitionRecord> _history = new();
    private readonly Dictionary<string, PatternCandidate> _candidates = new();
    private readonly int _maxHistorySize;
    private readonly float _minSupportThreshold;
    private readonly float _minConfidenceThreshold;

    private int _nextPatternId;

    public int HistorySize => _history.Count;
    public int CandidateCount => _candidates.Count;

    public PatternDetector(
        int maxHistorySize = 10000,
        float minSupportThreshold = 0.01f,
        float minConfidenceThreshold = 0.7f)
    {
        _maxHistorySize = maxHistorySize;
        _minSupportThreshold = minSupportThreshold;
        _minConfidenceThreshold = minConfidenceThreshold;
    }

    /// <summary>
    /// Record a state transition for pattern analysis.
    /// </summary>
    public void RecordTransition(
        float[] state,
        int action,
        float reward,
        float[] nextState,
        bool terminal,
        Dictionary<string, float>? context = null)
    {
        var record = new TransitionRecord
        {
            State = state,
            Action = action,
            Reward = reward,
            NextState = nextState,
            Terminal = terminal,
            Context = context ?? new(),
            Timestamp = DateTime.UtcNow
        };

        _history.Add(record);

        // Trim history if too large
        while (_history.Count > _maxHistorySize)
        {
            _history.RemoveAt(0);
        }

        // Update pattern candidates based on this transition
        UpdateCandidates(record);
    }

    /// <summary>
    /// Analyze history and discover potential patterns.
    /// Returns new knowledge units that meet thresholds.
    /// </summary>
    public List<KnowledgeUnit> DiscoverPatterns()
    {
        var discovered = new List<KnowledgeUnit>();

        // Discover state-reward correlations (potential Laws/Rules)
        discovered.AddRange(DiscoverRewardCorrelations());

        // Discover action-outcome patterns (potential Policies)
        discovered.AddRange(DiscoverActionPatterns());

        // Discover conditional patterns (potential Conditions)
        discovered.AddRange(DiscoverConditionalPatterns());

        // Discover sequential patterns (potential Traditions)
        discovered.AddRange(DiscoverSequentialPatterns());

        return discovered;
    }

    /// <summary>
    /// Discover correlations between state features and rewards.
    /// These become Laws (if very strong) or Rules (if strong).
    /// </summary>
    private List<KnowledgeUnit> DiscoverRewardCorrelations()
    {
        var patterns = new List<KnowledgeUnit>();
        if (_history.Count < 100) return patterns;

        var featureCount = _history[0].State.Length;

        for (int i = 0; i < featureCount; i++)
        {
            // Calculate correlation between feature and reward
            var correlation = CalculateCorrelation(
                _history.Select(h => h.State[i]).ToArray(),
                _history.Select(h => h.Reward).ToArray());

            // Strong positive correlation - high feature values lead to positive rewards
            if (correlation > 0.5f)
            {
                var pattern = CreateFeatureRewardPattern(i, true, correlation);
                if (pattern != null) patterns.Add(pattern);
            }
            // Strong negative correlation - high feature values lead to negative rewards
            else if (correlation < -0.5f)
            {
                var pattern = CreateFeatureRewardPattern(i, false, -correlation);
                if (pattern != null) patterns.Add(pattern);
            }
        }

        return patterns;
    }

    /// <summary>
    /// Discover patterns between actions and outcomes.
    /// These become Policies.
    /// </summary>
    private List<KnowledgeUnit> DiscoverActionPatterns()
    {
        var patterns = new List<KnowledgeUnit>();
        if (_history.Count < 100) return patterns;

        // Group by action
        var actionGroups = _history.GroupBy(h => h.Action).ToList();

        foreach (var group in actionGroups)
        {
            var action = group.Key;
            var transitions = group.ToList();

            if (transitions.Count < 10) continue;

            // Find what state conditions make this action successful
            var successfulTransitions = transitions.Where(t => t.Reward > 0).ToList();
            var unsuccessfulTransitions = transitions.Where(t => t.Reward <= 0).ToList();

            if (successfulTransitions.Count < 5) continue;

            // Find distinguishing features
            var distinguishingFeatures = FindDistinguishingFeatures(
                successfulTransitions.Select(t => t.State).ToList(),
                unsuccessfulTransitions.Select(t => t.State).ToList());

            foreach (var (featureIdx, threshold, isGreater) in distinguishingFeatures)
            {
                var pattern = CreateActionConditionPattern(action, featureIdx, threshold, isGreater);
                if (pattern != null) patterns.Add(pattern);
            }
        }

        return patterns;
    }

    /// <summary>
    /// Discover if-then patterns (conditions that trigger outcomes).
    /// These become Conditions.
    /// </summary>
    private List<KnowledgeUnit> DiscoverConditionalPatterns()
    {
        var patterns = new List<KnowledgeUnit>();
        if (_history.Count < 100) return patterns;

        // Look for state transitions that reliably follow certain conditions
        var featureCount = _history[0].State.Length;

        for (int i = 0; i < featureCount; i++)
        {
            // Find transitions where feature i changed significantly
            var significantChanges = _history
                .Where(h => Math.Abs(h.NextState[i] - h.State[i]) > 0.1f)
                .ToList();

            if (significantChanges.Count < 10) continue;

            // What conditions precede this change?
            for (int j = 0; j < featureCount; j++)
            {
                if (i == j) continue;

                var correlation = CalculateCorrelation(
                    significantChanges.Select(h => h.State[j]).ToArray(),
                    significantChanges.Select(h => h.NextState[i] - h.State[i]).ToArray());

                if (Math.Abs(correlation) > 0.4f)
                {
                    var pattern = CreateConditionalPattern(j, i, correlation);
                    if (pattern != null) patterns.Add(pattern);
                }
            }
        }

        return patterns;
    }

    /// <summary>
    /// Discover sequential patterns (traditions that work in context).
    /// </summary>
    private List<KnowledgeUnit> DiscoverSequentialPatterns()
    {
        var patterns = new List<KnowledgeUnit>();
        if (_history.Count < 200) return patterns;

        // Look for action sequences that lead to positive outcomes
        for (int windowSize = 2; windowSize <= 4; windowSize++)
        {
            var sequences = new Dictionary<string, (int count, float totalReward)>();

            for (int i = 0; i <= _history.Count - windowSize; i++)
            {
                var window = _history.Skip(i).Take(windowSize).ToList();
                var sequenceKey = string.Join("→", window.Select(w => w.Action));
                var totalReward = window.Sum(w => w.Reward);

                if (sequences.TryGetValue(sequenceKey, out var existing))
                {
                    sequences[sequenceKey] = (existing.count + 1, existing.totalReward + totalReward);
                }
                else
                {
                    sequences[sequenceKey] = (1, totalReward);
                }
            }

            // Find sequences with positive average reward
            foreach (var (sequence, (count, totalReward)) in sequences)
            {
                if (count < 10) continue;

                float avgReward = totalReward / count;
                float support = (float)count / (_history.Count - windowSize + 1);

                if (avgReward > 0.1f && support > _minSupportThreshold)
                {
                    var pattern = CreateSequencePattern(sequence, avgReward, count);
                    if (pattern != null) patterns.Add(pattern);
                }
            }
        }

        return patterns;
    }

    private void UpdateCandidates(TransitionRecord record)
    {
        // Update existing candidates with this new evidence
        foreach (var candidate in _candidates.Values)
        {
            if (candidate.Pattern.Antecedent.Matches(record.State, record.Context))
            {
                // Check if consequent holds
                bool consequentHolds = candidate.Pattern.Consequent.Matches(record.NextState, record.Context);

                if (consequentHolds)
                    candidate.Confirmations++;
                else
                    candidate.Violations++;
            }
        }
    }

    private KnowledgeUnit? CreateFeatureRewardPattern(int featureIdx, bool positiveCorrelation, float strength)
    {
        var featureName = GetFeatureName(featureIdx);
        var op = positiveCorrelation ? ComparisonOp.GreaterThan : ComparisonOp.LessThan;
        var threshold = CalculateFeatureThreshold(featureIdx, positiveCorrelation);

        var level = strength > 0.8f ? KnowledgeLevel.Rule : KnowledgeLevel.Principle;

        return new KnowledgeUnit
        {
            Id = $"reward_corr_{_nextPatternId++}",
            Level = level,
            Description = $"When {featureName} is {(positiveCorrelation ? "high" : "low")}, outcomes tend to be {(positiveCorrelation ? "positive" : "negative")}",
            Antecedent = new KnowledgePattern
            {
                Expression = $"{featureName} {op} {threshold:F2}",
                Conditions = new List<PatternCondition>
                {
                    new() { Feature = featureName, Operator = op, Threshold = threshold, FeatureIndex = featureIdx }
                }
            },
            Consequent = new KnowledgePattern
            {
                Expression = positiveCorrelation ? "positive_reward" : "negative_reward",
                Conditions = new List<PatternCondition>()
            },
            Confidence = strength,
            Tags = new HashSet<string> { "reward", featureName }
        };
    }

    private KnowledgeUnit? CreateActionConditionPattern(int action, int featureIdx, float threshold, bool isGreater)
    {
        var featureName = GetFeatureName(featureIdx);
        var actionName = GetActionName(action);
        var op = isGreater ? ComparisonOp.GreaterThan : ComparisonOp.LessThan;

        return new KnowledgeUnit
        {
            Id = $"action_cond_{_nextPatternId++}",
            Level = KnowledgeLevel.Policy,
            Description = $"Action '{actionName}' works better when {featureName} is {(isGreater ? "high" : "low")}",
            Antecedent = new KnowledgePattern
            {
                Expression = $"{featureName} {op} {threshold:F2}",
                Conditions = new List<PatternCondition>
                {
                    new() { Feature = featureName, Operator = op, Threshold = threshold, FeatureIndex = featureIdx }
                }
            },
            Consequent = new KnowledgePattern
            {
                Expression = $"action={actionName}",
                Conditions = new List<PatternCondition>
                {
                    new() { Feature = "suggested_action", Operator = ComparisonOp.Equal, Threshold = action }
                }
            },
            Confidence = 0.6f,
            Tags = new HashSet<string> { "policy", actionName, featureName }
        };
    }

    private KnowledgeUnit? CreateConditionalPattern(int conditionFeature, int effectFeature, float correlation)
    {
        var condName = GetFeatureName(conditionFeature);
        var effectName = GetFeatureName(effectFeature);
        var direction = correlation > 0 ? "increases" : "decreases";

        return new KnowledgeUnit
        {
            Id = $"conditional_{_nextPatternId++}",
            Level = KnowledgeLevel.Condition,
            Description = $"When {condName} is high, {effectName} tends to {direction}",
            Antecedent = new KnowledgePattern
            {
                Expression = $"{condName} > 0.5",
                Conditions = new List<PatternCondition>
                {
                    new() { Feature = condName, Operator = ComparisonOp.GreaterThan, Threshold = 0.5f, FeatureIndex = conditionFeature }
                }
            },
            Consequent = new KnowledgePattern
            {
                Expression = $"{effectName} {direction}",
                Conditions = new List<PatternCondition>()
            },
            Confidence = Math.Abs(correlation),
            Tags = new HashSet<string> { "conditional", condName, effectName }
        };
    }

    private KnowledgeUnit? CreateSequencePattern(string sequence, float avgReward, int count)
    {
        return new KnowledgeUnit
        {
            Id = $"sequence_{_nextPatternId++}",
            Level = KnowledgeLevel.Tradition,
            Description = $"Action sequence [{sequence}] tends to produce positive outcomes",
            Antecedent = new KnowledgePattern
            {
                Expression = "any",
                Conditions = new List<PatternCondition>()
            },
            Consequent = new KnowledgePattern
            {
                Expression = $"sequence={sequence}",
                Conditions = new List<PatternCondition>()
            },
            Confidence = Math.Min(0.9f, avgReward),
            Confirmations = count,
            Tags = new HashSet<string> { "sequence", "tradition" }
        };
    }

    private float CalculateCorrelation(float[] x, float[] y)
    {
        if (x.Length != y.Length || x.Length < 2) return 0;

        float meanX = x.Average();
        float meanY = y.Average();

        float sumXY = 0, sumX2 = 0, sumY2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            float dx = x[i] - meanX;
            float dy = y[i] - meanY;
            sumXY += dx * dy;
            sumX2 += dx * dx;
            sumY2 += dy * dy;
        }

        float denom = (float)Math.Sqrt(sumX2 * sumY2);
        return denom > 0.0001f ? sumXY / denom : 0;
    }

    private List<(int featureIdx, float threshold, bool isGreater)> FindDistinguishingFeatures(
        List<float[]> positive,
        List<float[]> negative)
    {
        var results = new List<(int, float, bool)>();
        if (positive.Count == 0 || negative.Count == 0) return results;

        int featureCount = positive[0].Length;

        for (int i = 0; i < featureCount; i++)
        {
            float posMean = positive.Select(p => p[i]).Average();
            float negMean = negative.Select(n => n[i]).Average();
            float diff = posMean - negMean;

            if (Math.Abs(diff) > 0.15f)
            {
                float threshold = (posMean + negMean) / 2;
                results.Add((i, threshold, diff > 0));
            }
        }

        return results;
    }

    private float CalculateFeatureThreshold(int featureIdx, bool upper)
    {
        var values = _history.Select(h => h.State[featureIdx]).OrderBy(v => v).ToList();
        int percentileIdx = upper ? (int)(values.Count * 0.75) : (int)(values.Count * 0.25);
        return values[Math.Clamp(percentileIdx, 0, values.Count - 1)];
    }

    private static string GetFeatureName(int idx) => idx switch
    {
        FeatureIndices.Health => "health",
        FeatureIndices.Armor => "armor",
        FeatureIndices.AmmoClip => "ammo_clip",
        FeatureIndices.AmmoReserve => "ammo_reserve",
        FeatureIndices.IsReloading => "is_reloading",
        FeatureIndices.ThreatsInFov => "threats_in_fov",
        FeatureIndices.NearestThreatDist => "nearest_threat_dist",
        FeatureIndices.DangerLevel => "danger_level",
        FeatureIndices.HasTarget => "has_target",
        FeatureIndices.IsOnTarget => "is_on_target",
        FeatureIndices.PixelDistance => "pixel_distance",
        FeatureIndices.TargetConfidence => "target_confidence",
        FeatureIndices.AimOffsetX => "aim_offset_x",
        FeatureIndices.AimOffsetY => "aim_offset_y",
        FeatureIndices.IsStuck => "is_stuck",
        FeatureIndices.FramesSinceHit => "frames_since_hit",
        FeatureIndices.FramesSinceDamage => "frames_since_damage",
        FeatureIndices.Wave => "wave",
        _ => $"feature_{idx}"
    };

    private static string GetActionName(int action) => action switch
    {
        0 => "Idle",
        1 => "Fight",
        2 => "Kite",
        3 => "Reload",
        4 => "Heal",
        5 => "Repair",
        6 => "Reposition",
        7 => "Buy",
        8 => "Support",
        9 => "Unstick",
        _ => $"Action_{action}"
    };
}

/// <summary>
/// A recorded state transition.
/// </summary>
public sealed class TransitionRecord
{
    public required float[] State { get; init; }
    public required int Action { get; init; }
    public required float Reward { get; init; }
    public required float[] NextState { get; init; }
    public required bool Terminal { get; init; }
    public Dictionary<string, float> Context { get; init; } = new();
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// A candidate pattern being evaluated.
/// </summary>
public sealed class PatternCandidate
{
    public required KnowledgeUnit Pattern { get; init; }
    public int Confirmations { get; set; }
    public int Violations { get; set; }
    public float Confidence => Confirmations + Violations > 0
        ? (float)Confirmations / (Confirmations + Violations)
        : 0.5f;
}
