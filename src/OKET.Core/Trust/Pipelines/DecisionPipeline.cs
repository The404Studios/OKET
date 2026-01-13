namespace OKET.Core.Trust.Pipelines;

/// <summary>
/// Decision Pipeline - Raw decision making without trust validation.
///
/// DECISION PROCESS:
///   [Situation Assessment]
///        ↓
///   [Option Generation]
///        ↓
///   [Utility Evaluation]
///        ↓
///   [Risk Assessment]
///        ↓
///   [Decision Output]
///
/// Decisions are PROPOSALS until validated by TrustedDecisionPipeline.
/// A raw decision may be wrong, biased, or based on faulty perception.
/// </summary>
public sealed class DecisionPipeline
{
    // Option generators
    private readonly List<IOptionGenerator> _optionGenerators = new();

    // Utility function weights
    private float _rewardWeight = 1.0f;
    private float _riskWeight = 0.8f;
    private float _urgencyWeight = 0.6f;
    private float _noveltyWeight = 0.3f;
    private float _habitWeight = 0.4f;

    // History for pattern detection
    private readonly Queue<DecisionRecord> _history = new();
    private const int MaxHistory = 100;

    // Statistics
    private int _totalDecisions;
    private int _uniqueOptions;
    private float _avgConfidence;

    public int TotalDecisions => _totalDecisions;
    public float AvgConfidence => _avgConfidence;

    public DecisionPipeline()
    {
        InitializeDefaultGenerators();
    }

    private void InitializeDefaultGenerators()
    {
        _optionGenerators.Add(new ThreatResponseGenerator());
        _optionGenerators.Add(new OpportunitySeekGenerator());
        _optionGenerators.Add(new SurvivalGenerator());
        _optionGenerators.Add(new ExplorationGenerator());
        _optionGenerators.Add(new HabitGenerator());
    }

    /// <summary>
    /// Generate a decision proposal from current situation.
    /// </summary>
    public DecisionProposal Decide(DecisionContext context)
    {
        _totalDecisions++;

        // === STAGE 1: SITUATION ASSESSMENT ===
        var assessment = AssessSituation(context);

        // === STAGE 2: OPTION GENERATION ===
        var options = GenerateOptions(context, assessment);
        _uniqueOptions += options.Count;

        if (options.Count == 0)
        {
            return DecisionProposal.Default(DecisionType.Observe, 0.5f, "No options generated");
        }

        // === STAGE 3: UTILITY EVALUATION ===
        foreach (var option in options)
        {
            option.Utility = ComputeUtility(option, context, assessment);
        }

        // === STAGE 4: RISK ASSESSMENT ===
        foreach (var option in options)
        {
            option.Risk = AssessRisk(option, context);
            option.AdjustedUtility = option.Utility - _riskWeight * option.Risk;
        }

        // === STAGE 5: SELECT BEST OPTION ===
        var bestOption = options.OrderByDescending(o => o.AdjustedUtility).First();

        // Compute confidence
        float confidence = ComputeConfidence(bestOption, options, assessment);
        _avgConfidence = _avgConfidence * 0.99f + confidence * 0.01f;

        // Record for pattern detection
        RecordDecision(bestOption, context, confidence);

        return new DecisionProposal
        {
            Type = bestOption.Type,
            Target = bestOption.Target,
            Confidence = confidence,
            Utility = bestOption.AdjustedUtility,
            Risk = bestOption.Risk,
            Reasoning = bestOption.Reasoning,
            Alternatives = options.Where(o => o != bestOption).Take(3).ToList(),
            Assessment = assessment,
            IsHabitDriven = bestOption.Source == OptionSource.Habit,
            IsNoveltyDriven = bestOption.Source == OptionSource.Exploration
        };
    }

    /// <summary>
    /// Assess current situation.
    /// </summary>
    private static SituationAssessment AssessSituation(DecisionContext context)
    {
        // Threat level
        float threatLevel = context.ThreatCount / 5f + (context.UnderAttack ? 0.3f : 0f);
        threatLevel = Math.Clamp(threatLevel, 0f, 1f);

        // Opportunity level
        float opportunityLevel = context.OpportunityCount / 3f;
        opportunityLevel = Math.Clamp(opportunityLevel, 0f, 1f);

        // Resource status
        float resourceStatus = (context.Health + context.Ammo) / 2f;

        // Urgency (time pressure)
        float urgency = threatLevel * (1f - context.Health) + (context.UnderAttack ? 0.4f : 0f);

        // Stability (how much has changed)
        float stability = 1f - context.PerceptionVolatility;

        return new SituationAssessment
        {
            ThreatLevel = threatLevel,
            OpportunityLevel = opportunityLevel,
            ResourceStatus = resourceStatus,
            Urgency = Math.Clamp(urgency, 0f, 1f),
            Stability = stability,
            Complexity = context.ThreatCount + context.OpportunityCount,
            TimeHorizon = urgency > 0.7f ? TimeHorizon.Immediate : TimeHorizon.Short
        };
    }

    /// <summary>
    /// Generate decision options.
    /// </summary>
    private List<DecisionOption> GenerateOptions(
        DecisionContext context,
        SituationAssessment assessment)
    {
        var options = new List<DecisionOption>();

        foreach (var generator in _optionGenerators)
        {
            if (generator.IsApplicable(context, assessment))
            {
                var generated = generator.Generate(context, assessment);
                options.AddRange(generated);
            }
        }

        return options;
    }

    /// <summary>
    /// Compute utility for an option.
    /// </summary>
    private float ComputeUtility(
        DecisionOption option,
        DecisionContext context,
        SituationAssessment assessment)
    {
        float utility = 0;

        // Expected reward
        utility += _rewardWeight * option.ExpectedReward;

        // Urgency alignment
        if (assessment.Urgency > 0.5f && option.Type.IsUrgentAction())
            utility += _urgencyWeight * assessment.Urgency;

        // Habit bonus (faster execution, lower cognitive load)
        if (option.Source == OptionSource.Habit)
            utility += _habitWeight * option.HabitStrength;

        // Novelty value (information gain)
        if (option.Source == OptionSource.Exploration)
            utility += _noveltyWeight * option.NoveltyValue;

        return utility;
    }

    /// <summary>
    /// Assess risk for an option.
    /// </summary>
    private static float AssessRisk(DecisionOption option, DecisionContext context)
    {
        float risk = option.InherentRisk;

        // Low health amplifies risk
        if (context.Health < 0.3f)
            risk *= 1.5f;

        // Unknown outcomes increase risk
        if (option.OutcomeUncertainty > 0.5f)
            risk += 0.2f;

        // Active threats increase engagement risk
        if (option.Type == DecisionType.Engage && context.UnderAttack)
            risk += 0.1f;

        return Math.Clamp(risk, 0f, 1f);
    }

    /// <summary>
    /// Compute decision confidence.
    /// </summary>
    private static float ComputeConfidence(
        DecisionOption best,
        List<DecisionOption> options,
        SituationAssessment assessment)
    {
        // Base confidence from option
        float confidence = best.Confidence;

        // Dominance factor (how much better than alternatives)
        if (options.Count > 1)
        {
            var second = options.OrderByDescending(o => o.AdjustedUtility).Skip(1).First();
            float gap = best.AdjustedUtility - second.AdjustedUtility;
            confidence *= 1f + gap;
        }

        // Stability factor
        confidence *= assessment.Stability;

        return Math.Clamp(confidence, 0f, 1f);
    }

    /// <summary>
    /// Record decision for pattern learning.
    /// </summary>
    private void RecordDecision(
        DecisionOption option,
        DecisionContext context,
        float confidence)
    {
        _history.Enqueue(new DecisionRecord
        {
            Type = option.Type,
            Context = context,
            Confidence = confidence,
            Timestamp = DateTime.UtcNow
        });

        while (_history.Count > MaxHistory)
            _history.Dequeue();
    }

    /// <summary>
    /// Get recent decision patterns.
    /// </summary>
    public DecisionPattern GetRecentPattern(int windowSize = 10)
    {
        var recent = _history.TakeLast(windowSize).ToList();
        if (recent.Count == 0)
            return new DecisionPattern();

        return new DecisionPattern
        {
            DominantType = recent
                .GroupBy(r => r.Type)
                .OrderByDescending(g => g.Count())
                .First().Key,
            Consistency = recent
                .GroupBy(r => r.Type)
                .Max(g => g.Count()) / (float)recent.Count,
            AvgConfidence = recent.Average(r => r.Confidence),
            DecisionRate = recent.Count / (float)windowSize
        };
    }

    /// <summary>
    /// Update weights based on outcomes.
    /// </summary>
    public void UpdateWeights(float rewardOutcome, float riskOutcome)
    {
        // Simple online learning
        float lr = 0.01f;

        if (rewardOutcome > 0.5f)
            _rewardWeight = Math.Min(1.5f, _rewardWeight + lr);
        else
            _rewardWeight = Math.Max(0.5f, _rewardWeight - lr);

        if (riskOutcome > 0.5f)
            _riskWeight = Math.Min(1.5f, _riskWeight + lr);
        else
            _riskWeight = Math.Max(0.3f, _riskWeight - lr);
    }

    public string GetDiagnostics()
    {
        var pattern = GetRecentPattern();
        return $"""
            === DECISION PIPELINE ===
            Decisions: {_totalDecisions}
            Avg Confidence: {_avgConfidence:F2}
            Pattern: {pattern.DominantType} ({pattern.Consistency:P0})
            Weights: reward={_rewardWeight:F2} risk={_riskWeight:F2}
            =========================
            """;
    }
}

/// <summary>
/// Context for decision making.
/// </summary>
public readonly struct DecisionContext
{
    public float Health { get; init; }
    public float Ammo { get; init; }
    public int ThreatCount { get; init; }
    public int OpportunityCount { get; init; }
    public bool UnderAttack { get; init; }
    public float PerceptionVolatility { get; init; }
    public float SystemStrain { get; init; }
    public List<int> VisibleTargetIds { get; init; }
    public int? PrimaryThreatId { get; init; }
    public int? PrimaryOpportunityId { get; init; }
}

/// <summary>
/// Situation assessment result.
/// </summary>
public readonly struct SituationAssessment
{
    public float ThreatLevel { get; init; }
    public float OpportunityLevel { get; init; }
    public float ResourceStatus { get; init; }
    public float Urgency { get; init; }
    public float Stability { get; init; }
    public int Complexity { get; init; }
    public TimeHorizon TimeHorizon { get; init; }
}

/// <summary>
/// Time horizon for planning.
/// </summary>
public enum TimeHorizon
{
    Immediate,  // React now
    Short,      // Next few seconds
    Medium,     // Next minute
    Long        // Strategic
}

/// <summary>
/// Decision option generated by a generator.
/// </summary>
public sealed class DecisionOption
{
    public DecisionType Type { get; init; }
    public int? Target { get; init; }
    public OptionSource Source { get; init; }
    public float ExpectedReward { get; init; }
    public float InherentRisk { get; init; }
    public float OutcomeUncertainty { get; init; }
    public float Confidence { get; init; }
    public float HabitStrength { get; init; }
    public float NoveltyValue { get; init; }
    public string Reasoning { get; init; } = "";

    // Computed
    public float Utility { get; set; }
    public float Risk { get; set; }
    public float AdjustedUtility { get; set; }
}

/// <summary>
/// Source of a decision option.
/// </summary>
public enum OptionSource
{
    ThreatResponse,
    OpportunitySeeking,
    Survival,
    Exploration,
    Habit
}

/// <summary>
/// Decision proposal output.
/// </summary>
public readonly struct DecisionProposal
{
    public DecisionType Type { get; init; }
    public int? Target { get; init; }
    public float Confidence { get; init; }
    public float Utility { get; init; }
    public float Risk { get; init; }
    public string Reasoning { get; init; }
    public List<DecisionOption> Alternatives { get; init; }
    public SituationAssessment Assessment { get; init; }
    public bool IsHabitDriven { get; init; }
    public bool IsNoveltyDriven { get; init; }

    public static DecisionProposal Default(DecisionType type, float confidence, string reason) =>
        new()
        {
            Type = type,
            Confidence = confidence,
            Reasoning = reason,
            Alternatives = new List<DecisionOption>()
        };
}

/// <summary>
/// Decision type.
/// </summary>
public enum DecisionType
{
    Observe,
    Engage,
    Flee,
    Kite,
    Approach,
    Interact,
    Explore,
    Wait,
    Stabilize
}

public static class DecisionTypeExtensions
{
    public static bool IsUrgentAction(this DecisionType type) =>
        type is DecisionType.Engage or DecisionType.Flee or DecisionType.Kite;

    public static ActionId ToActionId(this DecisionType type) =>
        type switch
        {
            DecisionType.Observe => ActionId.Observe,
            DecisionType.Engage => ActionId.Engage,
            DecisionType.Flee => ActionId.Flee,
            DecisionType.Kite => ActionId.Kite,
            DecisionType.Approach => ActionId.Approach,
            DecisionType.Interact => ActionId.Interact,
            DecisionType.Explore => ActionId.Probe,
            DecisionType.Wait => ActionId.Observe,
            DecisionType.Stabilize => ActionId.Observe,
            _ => ActionId.Observe
        };
}

/// <summary>
/// Record of a past decision.
/// </summary>
internal readonly struct DecisionRecord
{
    public DecisionType Type { get; init; }
    public DecisionContext Context { get; init; }
    public float Confidence { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Pattern detected in recent decisions.
/// </summary>
public readonly struct DecisionPattern
{
    public DecisionType DominantType { get; init; }
    public float Consistency { get; init; }
    public float AvgConfidence { get; init; }
    public float DecisionRate { get; init; }
}

/// <summary>
/// Interface for option generators.
/// </summary>
public interface IOptionGenerator
{
    bool IsApplicable(DecisionContext context, SituationAssessment assessment);
    List<DecisionOption> Generate(DecisionContext context, SituationAssessment assessment);
}

/// <summary>
/// Generates threat response options.
/// </summary>
internal sealed class ThreatResponseGenerator : IOptionGenerator
{
    public bool IsApplicable(DecisionContext context, SituationAssessment assessment) =>
        assessment.ThreatLevel > 0.1f;

    public List<DecisionOption> Generate(DecisionContext context, SituationAssessment assessment)
    {
        var options = new List<DecisionOption>();

        // Engage option
        if (context.Ammo > 0.1f)
        {
            options.Add(new DecisionOption
            {
                Type = DecisionType.Engage,
                Target = context.PrimaryThreatId,
                Source = OptionSource.ThreatResponse,
                ExpectedReward = 0.6f * context.Ammo,
                InherentRisk = 0.3f + assessment.ThreatLevel * 0.3f,
                OutcomeUncertainty = 0.3f,
                Confidence = context.Ammo * 0.8f,
                Reasoning = "Engage primary threat"
            });
        }

        // Kite option (always available for threats)
        options.Add(new DecisionOption
        {
            Type = DecisionType.Kite,
            Target = context.PrimaryThreatId,
            Source = OptionSource.ThreatResponse,
            ExpectedReward = 0.4f,
            InherentRisk = 0.2f,
            OutcomeUncertainty = 0.2f,
            Confidence = 0.7f,
            Reasoning = "Maintain distance while engaging"
        });

        // Flee option (if health low or overwhelmed)
        if (context.Health < 0.3f || context.ThreatCount > 3)
        {
            options.Add(new DecisionOption
            {
                Type = DecisionType.Flee,
                Source = OptionSource.Survival,
                ExpectedReward = 0.3f,
                InherentRisk = 0.15f,
                OutcomeUncertainty = 0.4f,
                Confidence = 0.6f,
                Reasoning = "Retreat to safety"
            });
        }

        return options;
    }
}

/// <summary>
/// Generates opportunity-seeking options.
/// </summary>
internal sealed class OpportunitySeekGenerator : IOptionGenerator
{
    public bool IsApplicable(DecisionContext context, SituationAssessment assessment) =>
        assessment.OpportunityLevel > 0.1f && assessment.ThreatLevel < 0.5f;

    public List<DecisionOption> Generate(DecisionContext context, SituationAssessment assessment)
    {
        var options = new List<DecisionOption>();

        if (context.PrimaryOpportunityId.HasValue)
        {
            // Approach opportunity
            options.Add(new DecisionOption
            {
                Type = DecisionType.Approach,
                Target = context.PrimaryOpportunityId,
                Source = OptionSource.OpportunitySeeking,
                ExpectedReward = 0.7f * assessment.OpportunityLevel,
                InherentRisk = assessment.ThreatLevel * 0.3f,
                OutcomeUncertainty = 0.2f,
                Confidence = 0.8f - assessment.ThreatLevel,
                Reasoning = "Approach resource/item"
            });

            // Interact if close
            if (assessment.ThreatLevel < 0.2f)
            {
                options.Add(new DecisionOption
                {
                    Type = DecisionType.Interact,
                    Target = context.PrimaryOpportunityId,
                    Source = OptionSource.OpportunitySeeking,
                    ExpectedReward = 0.9f * assessment.OpportunityLevel,
                    InherentRisk = 0.1f,
                    OutcomeUncertainty = 0.1f,
                    Confidence = 0.9f,
                    Reasoning = "Collect resource"
                });
            }
        }

        return options;
    }
}

/// <summary>
/// Generates survival-focused options.
/// </summary>
internal sealed class SurvivalGenerator : IOptionGenerator
{
    public bool IsApplicable(DecisionContext context, SituationAssessment assessment) =>
        context.Health < 0.5f || assessment.Urgency > 0.5f;

    public List<DecisionOption> Generate(DecisionContext context, SituationAssessment assessment)
    {
        var options = new List<DecisionOption>();

        // Defensive observation
        options.Add(new DecisionOption
        {
            Type = DecisionType.Observe,
            Source = OptionSource.Survival,
            ExpectedReward = 0.2f,
            InherentRisk = 0.05f,
            OutcomeUncertainty = 0.1f,
            Confidence = 0.9f,
            Reasoning = "Assess situation before acting"
        });

        // Stabilize if high strain
        if (context.SystemStrain > 1f)
        {
            options.Add(new DecisionOption
            {
                Type = DecisionType.Stabilize,
                Source = OptionSource.Survival,
                ExpectedReward = 0.3f,
                InherentRisk = 0.1f,
                OutcomeUncertainty = 0.2f,
                Confidence = 0.7f,
                Reasoning = "Reduce cognitive strain"
            });
        }

        return options;
    }
}

/// <summary>
/// Generates exploration options.
/// </summary>
internal sealed class ExplorationGenerator : IOptionGenerator
{
    public bool IsApplicable(DecisionContext context, SituationAssessment assessment) =>
        assessment.ThreatLevel < 0.3f && context.Health > 0.5f;

    public List<DecisionOption> Generate(DecisionContext context, SituationAssessment assessment)
    {
        var options = new List<DecisionOption>();

        options.Add(new DecisionOption
        {
            Type = DecisionType.Explore,
            Source = OptionSource.Exploration,
            ExpectedReward = 0.3f,
            InherentRisk = 0.15f,
            OutcomeUncertainty = 0.5f,
            Confidence = 0.6f,
            NoveltyValue = 0.7f,
            Reasoning = "Explore unknown area"
        });

        return options;
    }
}

/// <summary>
/// Generates habit-based options from learned patterns.
/// </summary>
internal sealed class HabitGenerator : IOptionGenerator
{
    private readonly Dictionary<int, DecisionType> _situationHabits = new();
    private readonly Dictionary<int, float> _habitStrengths = new();

    public bool IsApplicable(DecisionContext context, SituationAssessment assessment) => true;

    public List<DecisionOption> Generate(DecisionContext context, SituationAssessment assessment)
    {
        var options = new List<DecisionOption>();

        int situationHash = HashSituation(context, assessment);
        if (_situationHabits.TryGetValue(situationHash, out var habitType) &&
            _habitStrengths.TryGetValue(situationHash, out var strength) &&
            strength > 0.3f)
        {
            options.Add(new DecisionOption
            {
                Type = habitType,
                Source = OptionSource.Habit,
                ExpectedReward = 0.5f,
                InherentRisk = 0.2f,
                OutcomeUncertainty = 0.3f,
                Confidence = strength,
                HabitStrength = strength,
                Reasoning = $"Habitual response (strength={strength:F2})"
            });
        }

        return options;
    }

    public void ReinforcedHabit(DecisionContext context, SituationAssessment assessment, DecisionType type, float reward)
    {
        int hash = HashSituation(context, assessment);

        if (!_situationHabits.ContainsKey(hash))
        {
            _situationHabits[hash] = type;
            _habitStrengths[hash] = 0.3f;
        }
        else if (_situationHabits[hash] == type)
        {
            _habitStrengths[hash] = Math.Min(1f, _habitStrengths[hash] + reward * 0.1f);
        }
        else
        {
            _habitStrengths[hash] *= 0.9f; // Decay conflicting habit
        }
    }

    private static int HashSituation(DecisionContext context, SituationAssessment assessment)
    {
        // Discretize into buckets
        int healthBucket = (int)(context.Health * 4);
        int threatBucket = (int)(assessment.ThreatLevel * 4);
        int oppBucket = (int)(assessment.OpportunityLevel * 4);
        int urgencyBucket = (int)(assessment.Urgency * 4);

        return HashCode.Combine(healthBucket, threatBucket, oppBucket, urgencyBucket);
    }
}
