using OKET.Core.Detection;

namespace OKET.Core.Cognition;

/// <summary>
/// Learns and predicts proper reactions based on experience.
///
/// CORE PRINCIPLE: Prediction becomes understood over time.
/// The more we encounter a situation, the better we predict the proper reaction.
///
/// Learning occurs at multiple levels:
/// 1. Per-object: What works against this specific tracked object
/// 2. Per-class: What works against this type of object (zombie, item, etc.)
/// 3. Per-context: What works in this situation (zone, health, threats, etc.)
/// 4. Global: What generally works across all situations
///
/// The predictor feeds back into the gate system:
/// - High confidence predictions → faster gate response
/// - Low confidence → more cautious, slower commitment
/// </summary>
public sealed class ReactionPredictor
{
    // Context patterns learned over time
    private readonly Dictionary<ContextPattern, ReactionLearning> _contextLearning = new();

    // Temporal patterns (what happens after action sequences)
    private readonly Queue<ActionSequence> _sequenceHistory = new();
    private const int MaxSequenceHistory = 50;

    // Current prediction state
    private ReactionPrediction _currentPrediction = new();

    // Learning parameters
    private float _learningRate = 0.1f;
    private float _explorationRate = 0.15f;
    private int _totalPredictions;
    private int _correctPredictions;

    /// <summary>Current prediction for the situation.</summary>
    public ReactionPrediction CurrentPrediction => _currentPrediction;

    /// <summary>Overall prediction accuracy [0, 1].</summary>
    public float Accuracy => _totalPredictions > 0
        ? (float)_correctPredictions / _totalPredictions
        : 0.5f;

    /// <summary>How much exploration vs exploitation.</summary>
    public float ExplorationRate => _explorationRate;

    /// <summary>
    /// Predict the proper reaction for the current situation.
    /// </summary>
    public ReactionPrediction Predict(
        ThoughtManager thoughts,
        float health,
        float ammo,
        float systemStrain,
        float gateGain)
    {
        // Build context pattern from current state
        var context = BuildContext(thoughts, health, ammo, systemStrain, gateGain);

        // Look up learned reaction for this context
        ReactionLearning? learning = null;
        float bestMatchScore = 0;

        foreach (var (pattern, learn) in _contextLearning)
        {
            float matchScore = pattern.SimilarityTo(context);
            if (matchScore > bestMatchScore && matchScore > 0.6f)
            {
                bestMatchScore = matchScore;
                learning = learn;
            }
        }

        _totalPredictions++;

        // If we have learned data, use it
        if (learning != null && learning.Confidence > 0.3f)
        {
            _currentPrediction = new ReactionPrediction
            {
                RecommendedReaction = learning.BestReaction,
                Confidence = learning.Confidence * bestMatchScore,
                TimeToAct = learning.AverageTimeToAct,
                ExpectedOutcome = learning.AverageOutcome,
                IsLearned = true,
                ContextMatch = bestMatchScore
            };
        }
        else
        {
            // Fall back to rule-based prediction
            _currentPrediction = PredictFromRules(thoughts, health, ammo, systemStrain);
        }

        // Apply exploration noise
        if (Random.Shared.NextDouble() < _explorationRate)
        {
            _currentPrediction = _currentPrediction with
            {
                ShouldExplore = true,
                AlternativeReaction = GetExplorationReaction(_currentPrediction.RecommendedReaction)
            };
        }

        return _currentPrediction;
    }

    /// <summary>
    /// Build a context pattern from the current state.
    /// </summary>
    private static ContextPattern BuildContext(
        ThoughtManager thoughts,
        float health,
        float ammo,
        float systemStrain,
        float gateGain)
    {
        return new ContextPattern
        {
            HealthBucket = DiscretizeHealth(health),
            AmmoBucket = DiscretizeAmmo(ammo),
            ThreatBucket = DiscretizeThreat(thoughts.TotalThreat),
            OpportunityBucket = DiscretizeOpportunity(thoughts.TotalOpportunity),
            ZoneState = thoughts.HasForcedAction
                ? ZoneState.Forced
                : thoughts.TransitionThoughts.Any()
                    ? ZoneState.Transition
                    : ZoneState.Safe,
            StrainLevel = DiscretizeStrain(systemStrain),
            GainState = gateGain >= 1f ? GainState.Positive : GainState.Negative
        };
    }

    private static int DiscretizeHealth(float health) => health switch
    {
        < 0.2f => 0,  // Critical
        < 0.4f => 1,  // Low
        < 0.7f => 2,  // Medium
        _ => 3        // High
    };

    private static int DiscretizeAmmo(float ammo) => ammo switch
    {
        < 0.1f => 0,  // Empty
        < 0.3f => 1,  // Low
        < 0.6f => 2,  // Medium
        _ => 3        // Full
    };

    private static int DiscretizeThreat(float threat) => threat switch
    {
        < 0.3f => 0,  // Low
        < 0.7f => 1,  // Medium
        < 1.5f => 2,  // High
        _ => 3        // Critical
    };

    private static int DiscretizeOpportunity(float opp) => opp switch
    {
        < 0.2f => 0,  // None
        < 0.5f => 1,  // Some
        _ => 2        // High
    };

    private static int DiscretizeStrain(float strain) => strain switch
    {
        < 0.5f => 0,  // Low
        < 1.0f => 1,  // Normal
        < 1.5f => 2,  // High
        _ => 3        // Overloaded
    };

    /// <summary>
    /// Predict reaction from rules when we don't have learned data.
    /// </summary>
    private static ReactionPrediction PredictFromRules(
        ThoughtManager thoughts,
        float health,
        float ammo,
        float systemStrain)
    {
        var mostUrgent = thoughts.MostUrgent;

        // Critical health - flee
        if (health < 0.2f && thoughts.TotalThreat > 0.3f)
        {
            return new ReactionPrediction
            {
                RecommendedReaction = Reaction.Flee,
                Confidence = 0.8f,
                TimeToAct = 1,
                ExpectedOutcome = 0.3f,
                IsLearned = false
            };
        }

        // Forced action with ammo - engage
        if (thoughts.HasForcedAction && ammo > 0.1f)
        {
            return new ReactionPrediction
            {
                RecommendedReaction = Reaction.Engage,
                Confidence = 0.7f,
                TimeToAct = mostUrgent?.PredictedTimeToAction ?? 30,
                ExpectedOutcome = 0.5f,
                IsLearned = false
            };
        }

        // Forced action no ammo - kite
        if (thoughts.HasForcedAction && ammo < 0.1f)
        {
            return new ReactionPrediction
            {
                RecommendedReaction = Reaction.Kite,
                Confidence = 0.6f,
                TimeToAct = 5,
                ExpectedOutcome = 0.3f,
                IsLearned = false
            };
        }

        // High strain - stabilize
        if (systemStrain > 1.5f)
        {
            return new ReactionPrediction
            {
                RecommendedReaction = Reaction.Stabilize,
                Confidence = 0.5f,
                TimeToAct = 30,
                ExpectedOutcome = 0.4f,
                IsLearned = false
            };
        }

        // Low health, opportunity present - seek
        if (health < 0.5f && thoughts.TotalOpportunity > 0.3f)
        {
            return new ReactionPrediction
            {
                RecommendedReaction = Reaction.Seek,
                Confidence = 0.6f,
                TimeToAct = 60,
                ExpectedOutcome = 0.6f,
                IsLearned = false
            };
        }

        // Default - observe
        return new ReactionPrediction
        {
            RecommendedReaction = Reaction.Observe,
            Confidence = 0.5f,
            TimeToAct = 60,
            ExpectedOutcome = 0.5f,
            IsLearned = false
        };
    }

    /// <summary>
    /// Get an alternative reaction for exploration.
    /// </summary>
    private static Reaction GetExplorationReaction(Reaction current)
    {
        var alternatives = current switch
        {
            Reaction.Engage => new[] { Reaction.Kite, Reaction.Observe },
            Reaction.Flee => new[] { Reaction.Kite, Reaction.Engage },
            Reaction.Kite => new[] { Reaction.Engage, Reaction.Flee },
            Reaction.Observe => new[] { Reaction.Seek, Reaction.Engage },
            Reaction.Seek => new[] { Reaction.Observe, Reaction.Engage },
            Reaction.Stabilize => new[] { Reaction.Observe, Reaction.Kite },
            _ => new[] { Reaction.Observe }
        };

        return alternatives[Random.Shared.Next(alternatives.Length)];
    }

    /// <summary>
    /// Record the outcome of a predicted reaction.
    /// </summary>
    public void RecordOutcome(
        ContextPattern context,
        Reaction reactionTaken,
        float outcome,
        int actualTimeToAct)
    {
        // Check if prediction was correct
        bool wasCorrect = Math.Abs(outcome - _currentPrediction.ExpectedOutcome) < 0.3f &&
                         reactionTaken == _currentPrediction.RecommendedReaction;
        if (wasCorrect)
            _correctPredictions++;

        // Find or create learning entry
        ContextPattern? bestMatch = null;
        float bestScore = 0;

        foreach (var pattern in _contextLearning.Keys)
        {
            float score = pattern.SimilarityTo(context);
            if (score > bestScore && score > 0.8f)
            {
                bestScore = score;
                bestMatch = pattern;
            }
        }

        if (bestMatch.HasValue)
        {
            // Update existing learning
            UpdateLearning(_contextLearning[bestMatch.Value], reactionTaken, outcome, actualTimeToAct);
        }
        else if (_contextLearning.Count < 500) // Limit memory
        {
            // Create new learning entry
            var learning = new ReactionLearning();
            UpdateLearning(learning, reactionTaken, outcome, actualTimeToAct);
            _contextLearning[context] = learning;
        }

        // Record sequence
        _sequenceHistory.Enqueue(new ActionSequence
        {
            Context = context,
            Reaction = reactionTaken,
            Outcome = outcome,
            TimeToAct = actualTimeToAct
        });

        while (_sequenceHistory.Count > MaxSequenceHistory)
            _sequenceHistory.Dequeue();

        // Adapt exploration rate based on accuracy
        if (Accuracy > 0.7f)
            _explorationRate = Math.Max(0.05f, _explorationRate - 0.01f);
        else if (Accuracy < 0.4f)
            _explorationRate = Math.Min(0.3f, _explorationRate + 0.01f);
    }

    /// <summary>
    /// Update learning entry with new outcome.
    /// </summary>
    private void UpdateLearning(
        ReactionLearning learning,
        Reaction reaction,
        float outcome,
        int timeToAct)
    {
        learning.TotalExperiences++;

        // Update reaction-specific outcomes
        var reactionOutcomes = learning.ReactionOutcomes.GetValueOrDefault(reaction,
            new ReactionOutcome { Reaction = reaction });

        reactionOutcomes.Count++;
        reactionOutcomes.TotalOutcome += outcome;
        reactionOutcomes.AverageOutcome =
            reactionOutcomes.AverageOutcome * 0.9f + outcome * 0.1f;

        learning.ReactionOutcomes[reaction] = reactionOutcomes;

        // Find best reaction
        float bestOutcome = float.MinValue;
        Reaction bestReaction = Reaction.Observe;

        foreach (var (r, o) in learning.ReactionOutcomes)
        {
            if (o.Count >= 3 && o.AverageOutcome > bestOutcome)
            {
                bestOutcome = o.AverageOutcome;
                bestReaction = r;
            }
        }

        learning.BestReaction = bestReaction;
        learning.AverageOutcome = learning.AverageOutcome * 0.95f + outcome * 0.05f;
        learning.AverageTimeToAct = learning.AverageTimeToAct * 0.9f + timeToAct * 0.1f;
        learning.Confidence = Math.Min(1f, learning.TotalExperiences / 20f);
    }

    /// <summary>
    /// Reset learning (use carefully).
    /// </summary>
    public void Reset()
    {
        _currentPrediction = new ReactionPrediction();
        // Note: We don't reset learning - that persists across episodes
    }

    /// <summary>
    /// Clear all learned data.
    /// </summary>
    public void ClearLearning()
    {
        _contextLearning.Clear();
        _sequenceHistory.Clear();
        _totalPredictions = 0;
        _correctPredictions = 0;
        _explorationRate = 0.15f;
    }

    /// <summary>
    /// Get diagnostic information.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"""
            === REACTION PREDICTOR ===
            Predictions: {_totalPredictions}, Correct: {_correctPredictions}
            Accuracy: {Accuracy:P1}
            Exploration Rate: {_explorationRate:F2}
            Learned Contexts: {_contextLearning.Count}
            Sequence History: {_sequenceHistory.Count}

            Current: {_currentPrediction.RecommendedReaction} (conf={_currentPrediction.Confidence:F2})
            Expected: {_currentPrediction.ExpectedOutcome:F2} in {_currentPrediction.TimeToAct} frames
            Learned: {_currentPrediction.IsLearned}, Explore: {_currentPrediction.ShouldExplore}
            ==========================
            """;
    }
}

/// <summary>
/// Prediction result for a reaction.
/// </summary>
public record struct ReactionPrediction
{
    /// <summary>The recommended reaction.</summary>
    public Reaction RecommendedReaction { get; init; }

    /// <summary>Confidence in this prediction [0, 1].</summary>
    public float Confidence { get; init; }

    /// <summary>Predicted time until action needed (frames).</summary>
    public int TimeToAct { get; init; }

    /// <summary>Expected outcome of this reaction [-1, 1].</summary>
    public float ExpectedOutcome { get; init; }

    /// <summary>Was this prediction learned from experience?</summary>
    public bool IsLearned { get; init; }

    /// <summary>How well does current context match learned pattern?</summary>
    public float ContextMatch { get; init; }

    /// <summary>Should we explore an alternative?</summary>
    public bool ShouldExplore { get; init; }

    /// <summary>Alternative reaction for exploration.</summary>
    public Reaction AlternativeReaction { get; init; }
}

/// <summary>
/// High-level reaction types.
/// </summary>
public enum Reaction
{
    /// <summary>Just observe, don't act.</summary>
    Observe,

    /// <summary>Engage enemies aggressively.</summary>
    Engage,

    /// <summary>Retreat/flee from threats.</summary>
    Flee,

    /// <summary>Kite - attack while moving.</summary>
    Kite,

    /// <summary>Seek resources/opportunities.</summary>
    Seek,

    /// <summary>Stabilize - reduce strain, recover.</summary>
    Stabilize
}

/// <summary>
/// Pattern representing a context situation.
/// </summary>
public readonly record struct ContextPattern
{
    public int HealthBucket { get; init; }
    public int AmmoBucket { get; init; }
    public int ThreatBucket { get; init; }
    public int OpportunityBucket { get; init; }
    public ZoneState ZoneState { get; init; }
    public int StrainLevel { get; init; }
    public GainState GainState { get; init; }

    public float SimilarityTo(ContextPattern other)
    {
        int matches = 0;
        int total = 7;

        if (HealthBucket == other.HealthBucket) matches++;
        else if (Math.Abs(HealthBucket - other.HealthBucket) == 1) matches += 0; // Adjacent = half match
        if (AmmoBucket == other.AmmoBucket) matches++;
        if (ThreatBucket == other.ThreatBucket) matches++;
        if (OpportunityBucket == other.OpportunityBucket) matches++;
        if (ZoneState == other.ZoneState) matches++;
        if (StrainLevel == other.StrainLevel) matches++;
        if (GainState == other.GainState) matches++;

        return matches / (float)total;
    }
}

public enum ZoneState { Safe, Transition, Forced }
public enum GainState { Negative, Positive }

/// <summary>
/// Learning data for a context pattern.
/// </summary>
internal sealed class ReactionLearning
{
    public int TotalExperiences { get; set; }
    public Reaction BestReaction { get; set; } = Reaction.Observe;
    public float AverageOutcome { get; set; }
    public float AverageTimeToAct { get; set; } = 30;
    public float Confidence { get; set; }
    public Dictionary<Reaction, ReactionOutcome> ReactionOutcomes { get; } = new();
}

internal struct ReactionOutcome
{
    public Reaction Reaction { get; set; }
    public int Count { get; set; }
    public float TotalOutcome { get; set; }
    public float AverageOutcome { get; set; }
}

internal readonly struct ActionSequence
{
    public ContextPattern Context { get; init; }
    public Reaction Reaction { get; init; }
    public float Outcome { get; init; }
    public int TimeToAct { get; init; }
}
