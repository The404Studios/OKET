namespace OKET.Agent.Learning.Knowledge;

/// <summary>
/// Hierarchy level for knowledge organization.
/// From most fundamental (Laws) to most contextual (Traditions).
/// </summary>
public enum KnowledgeLevel
{
    /// <summary>
    /// Laws: Fundamental invariants that always hold.
    /// Example: "Taking damage reduces health" - never violated.
    /// Highest confidence requirement, rarely modified.
    /// </summary>
    Law = 0,

    /// <summary>
    /// Rules: Strong patterns with high reliability.
    /// Example: "Shooting at zombies deals damage" - nearly always true.
    /// High confidence, modified only with strong contrary evidence.
    /// </summary>
    Rule = 1,

    /// <summary>
    /// Policies: Strategic guidelines for decision-making.
    /// Example: "Kite when health is low" - good general strategy.
    /// Medium-high confidence, adapts to meta-changes.
    /// </summary>
    Policy = 2,

    /// <summary>
    /// Conditions: Contextual triggers that activate behaviors.
    /// Example: "When ammo is empty → reload" - situational.
    /// Medium confidence, frequently updated.
    /// </summary>
    Condition = 3,

    /// <summary>
    /// Covenants: Self-imposed commitments/constraints.
    /// Example: "Never shoot teammates" - behavioral contract.
    /// High importance but can be revised.
    /// </summary>
    Covenant = 4,

    /// <summary>
    /// Principles: Guiding heuristics derived from experience.
    /// Example: "Moving targets are harder to hit" - learned wisdom.
    /// Medium confidence, evolves with experience.
    /// </summary>
    Principle = 5,

    /// <summary>
    /// Traditions: Proven successful patterns in specific contexts.
    /// Example: "Circle-strafe works against slow zombies" - tactical knowledge.
    /// Lower confidence, highly contextual, frequently tested.
    /// </summary>
    Tradition = 6
}

/// <summary>
/// A piece of organized knowledge discovered through experience.
/// </summary>
public sealed class KnowledgeUnit
{
    public required string Id { get; init; }
    public required KnowledgeLevel Level { get; init; }
    public required string Description { get; init; }

    /// <summary>Pattern that triggers this knowledge (conditions for relevance).</summary>
    public required KnowledgePattern Antecedent { get; init; }

    /// <summary>Expected outcome when pattern holds.</summary>
    public required KnowledgePattern Consequent { get; init; }

    /// <summary>Confidence in this knowledge [0, 1].</summary>
    public float Confidence { get; set; } = 0.5f;

    /// <summary>How many times this knowledge has been confirmed.</summary>
    public int Confirmations { get; set; }

    /// <summary>How many times this knowledge has been violated.</summary>
    public int Violations { get; set; }

    /// <summary>When this knowledge was first discovered.</summary>
    public DateTime DiscoveredAt { get; init; } = DateTime.UtcNow;

    /// <summary>When this knowledge was last confirmed.</summary>
    public DateTime LastConfirmedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this knowledge was last violated.</summary>
    public DateTime? LastViolatedAt { get; set; }

    /// <summary>Tags for categorization and retrieval.</summary>
    public HashSet<string> Tags { get; init; } = new();

    /// <summary>Related knowledge units (for graph traversal).</summary>
    public HashSet<string> RelatedIds { get; init; } = new();

    /// <summary>Reliability score based on confirmations vs violations.</summary>
    public float Reliability => Confirmations + Violations > 0
        ? (float)Confirmations / (Confirmations + Violations)
        : 0.5f;

    /// <summary>Whether this knowledge is stable enough to trust.</summary>
    public bool IsStable => Confirmations >= GetMinConfirmationsForLevel(Level)
                           && Reliability >= GetMinReliabilityForLevel(Level);

    /// <summary>Whether this knowledge should be promoted to a higher level.</summary>
    public bool ShouldPromote => IsStable && Reliability > 0.9f
                                && Confirmations > GetMinConfirmationsForLevel(Level) * 2;

    /// <summary>Whether this knowledge should be demoted to a lower level.</summary>
    public bool ShouldDemote => Reliability < GetMinReliabilityForLevel(Level) * 0.7f
                               && Violations > 5;

    private static int GetMinConfirmationsForLevel(KnowledgeLevel level) => level switch
    {
        KnowledgeLevel.Law => 100,
        KnowledgeLevel.Rule => 50,
        KnowledgeLevel.Policy => 30,
        KnowledgeLevel.Condition => 20,
        KnowledgeLevel.Covenant => 10,
        KnowledgeLevel.Principle => 25,
        KnowledgeLevel.Tradition => 15,
        _ => 20
    };

    private static float GetMinReliabilityForLevel(KnowledgeLevel level) => level switch
    {
        KnowledgeLevel.Law => 0.99f,
        KnowledgeLevel.Rule => 0.95f,
        KnowledgeLevel.Policy => 0.85f,
        KnowledgeLevel.Condition => 0.80f,
        KnowledgeLevel.Covenant => 0.90f,
        KnowledgeLevel.Principle => 0.75f,
        KnowledgeLevel.Tradition => 0.70f,
        _ => 0.75f
    };

    public void RecordConfirmation()
    {
        Confirmations++;
        LastConfirmedAt = DateTime.UtcNow;
        UpdateConfidence();
    }

    public void RecordViolation()
    {
        Violations++;
        LastViolatedAt = DateTime.UtcNow;
        UpdateConfidence();
    }

    private void UpdateConfidence()
    {
        // Bayesian-style update
        float prior = Confidence;
        float evidence = Reliability;
        float levelWeight = 1f - (int)Level / 10f; // Higher levels are more stable

        Confidence = prior * levelWeight + evidence * (1f - levelWeight);
        Confidence = Math.Clamp(Confidence, 0.01f, 0.99f);
    }

    public override string ToString() =>
        $"[{Level}] {Description} (conf={Confidence:F2}, rel={Reliability:F2}, n={Confirmations + Violations})";
}

/// <summary>
/// A pattern that can be matched against game state.
/// </summary>
public sealed class KnowledgePattern
{
    public required string Expression { get; init; }
    public List<PatternCondition> Conditions { get; init; } = new();

    /// <summary>
    /// Evaluate this pattern against a feature vector.
    /// </summary>
    public bool Matches(float[] features, Dictionary<string, float>? context = null)
    {
        foreach (var condition in Conditions)
        {
            if (!condition.Evaluate(features, context))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Calculate how well this pattern matches (0 = no match, 1 = perfect match).
    /// </summary>
    public float MatchStrength(float[] features, Dictionary<string, float>? context = null)
    {
        if (Conditions.Count == 0) return 1f;

        float totalStrength = 0;
        foreach (var condition in Conditions)
        {
            totalStrength += condition.EvaluateStrength(features, context);
        }
        return totalStrength / Conditions.Count;
    }
}

/// <summary>
/// A single condition in a pattern.
/// </summary>
public sealed class PatternCondition
{
    public required string Feature { get; init; }
    public required ComparisonOp Operator { get; init; }
    public required float Threshold { get; init; }
    public int? FeatureIndex { get; init; }

    public bool Evaluate(float[] features, Dictionary<string, float>? context)
    {
        float value = GetValue(features, context);
        return Operator switch
        {
            ComparisonOp.GreaterThan => value > Threshold,
            ComparisonOp.LessThan => value < Threshold,
            ComparisonOp.GreaterOrEqual => value >= Threshold,
            ComparisonOp.LessOrEqual => value <= Threshold,
            ComparisonOp.Equal => Math.Abs(value - Threshold) < 0.01f,
            ComparisonOp.NotEqual => Math.Abs(value - Threshold) >= 0.01f,
            ComparisonOp.InRange => value >= Threshold && value <= (context?.GetValueOrDefault($"{Feature}_max") ?? 1f),
            _ => false
        };
    }

    public float EvaluateStrength(float[] features, Dictionary<string, float>? context)
    {
        float value = GetValue(features, context);
        float diff = Math.Abs(value - Threshold);

        return Operator switch
        {
            ComparisonOp.GreaterThan => value > Threshold ? 1f : Math.Max(0, 1f - diff),
            ComparisonOp.LessThan => value < Threshold ? 1f : Math.Max(0, 1f - diff),
            ComparisonOp.Equal => Math.Max(0, 1f - diff * 10f),
            _ => Evaluate(features, context) ? 1f : 0f
        };
    }

    private float GetValue(float[] features, Dictionary<string, float>? context)
    {
        if (FeatureIndex.HasValue && FeatureIndex.Value < features.Length)
            return features[FeatureIndex.Value];

        if (context?.TryGetValue(Feature, out var ctxValue) == true)
            return ctxValue;

        return 0f;
    }
}

public enum ComparisonOp
{
    GreaterThan,
    LessThan,
    GreaterOrEqual,
    LessOrEqual,
    Equal,
    NotEqual,
    InRange
}

/// <summary>
/// Feature indices for the standard GameState feature vector.
/// </summary>
public static class FeatureIndices
{
    public const int Health = 0;
    public const int Armor = 1;
    public const int AmmoClip = 2;
    public const int AmmoReserve = 3;
    public const int IsReloading = 4;
    public const int ThreatsInFov = 5;
    public const int NearestThreatDist = 6;
    public const int DangerLevel = 7;
    public const int HasTarget = 8;
    public const int IsOnTarget = 9;
    public const int PixelDistance = 10;
    public const int TargetConfidence = 11;
    public const int AimOffsetX = 12;
    public const int AimOffsetY = 13;
    public const int IsStuck = 14;
    public const int FramesSinceHit = 15;
    public const int FramesSinceDamage = 16;
    public const int Wave = 17;
}
