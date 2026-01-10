namespace OKET.Core.Memory;

using OKET.Core.Operators;

/// <summary>
/// Unique identifier for a reference in the system.
/// References are "things thinking can point to" in real-time.
/// </summary>
public readonly record struct RefId(long Value)
{
    private static long _counter;

    public static RefId New() => new(Interlocked.Increment(ref _counter));

    public override string ToString() => $"ref:{Value}";
}

/// <summary>
/// Types of references the system can create.
///
/// These are the "nouns" of operational understanding:
/// - What thinking heard (audio events)
/// - What thinking saw (detections, frames)
/// - What thinking concluded (beliefs, commitments)
/// - What thinking did (actions, outcomes)
/// </summary>
public enum RefType
{
    /// <summary>A visual frame snapshot.</summary>
    Frame,

    /// <summary>An audio event (hit sound, footsteps, etc.).</summary>
    AudioEvent,

    /// <summary>A detection (entity seen in frame).</summary>
    Detection,

    /// <summary>A tracked entity over time.</summary>
    Track,

    /// <summary>A belief candidate before commitment.</summary>
    BeliefCandidate,

    /// <summary>A committed belief/mode (passed BeliefLock).</summary>
    Commitment,

    /// <summary>An action plan.</summary>
    ActionPlan,

    /// <summary>An action outcome (what happened after acting).</summary>
    ActionOutcome,

    /// <summary>A Z-score spike (significant deviation).</summary>
    ZSpike,

    /// <summary>A strain trend (rising/falling Z₄).</summary>
    StrainTrend,

    /// <summary>A multimodal agreement moment.</summary>
    Agreement,

    /// <summary>A contradiction (modalities disagree).</summary>
    Contradiction,

    /// <summary>An expectation gap (something missing that should be present).</summary>
    ExpectationGap
}

/// <summary>
/// A reference node in operational memory.
///
/// References are born local (Separate), earn validity through the sink,
/// and only then get promoted to global structure (Inherited).
///
/// CRITICAL LAWS:
/// 1. Demotion is FASTER than promotion (reality changes faster than truth stabilizes)
/// 2. Promotion requires DIVERSE survival (not just repetition in same conditions)
/// 3. Inherited is REVOCABLE (sustained gap pressure can demote)
///
/// This is "thinking's memory" - ears + eyes → coherent references.
/// </summary>
public sealed class ReferenceNode
{
    public RefId Id { get; }
    public RefType Type { get; }
    public long TicksCreated { get; }
    public DateTime TimeCreated { get; }

    /// <summary>
    /// Binding state (Separate → Associated → Inherited).
    /// Local references start Separate; global ones reach Inherited.
    /// </summary>
    public BindState Bind { get; private set; }

    /// <summary>
    /// Validity: did this reference carry load?
    /// Positive when Z₄ falls after commit and outcomes improve.
    /// Negative when Z₄ rises and outcomes worsen.
    /// </summary>
    public float Validity { get; private set; }

    /// <summary>
    /// Salience: how "loud" this reference is in working memory.
    /// Decays over time; refreshed by validation or attention.
    /// Low-salience references are pruned first.
    /// </summary>
    public float Salience { get; private set; }

    /// <summary>
    /// Tags attached to this reference.
    /// Tags are cheap labels that become boundaries if they predict survival.
    /// </summary>
    public HashSet<string> Tags { get; } = new();

    /// <summary>
    /// Metrics associated with this reference.
    /// e.g., threat=0.8, z4=1.2, confidence=0.9
    /// </summary>
    public Dictionary<string, float> Metrics { get; } = new();

    /// <summary>
    /// Links to other references (edges in the reference graph).
    /// e.g., Detection → Track, Commitment → ActionPlan → Outcome
    /// </summary>
    public List<RefId> Links { get; } = new();

    /// <summary>
    /// Parent reference (what this was derived from).
    /// </summary>
    public RefId? Parent { get; set; }

    /// <summary>
    /// How many times this reference has been validated.
    /// </summary>
    public int ValidationCount { get; private set; }

    /// <summary>
    /// How many times validation failed.
    /// </summary>
    public int FailureCount { get; private set; }

    /// <summary>
    /// Consecutive failures (resets on success).
    /// Used for rapid demotion - reality changes fast.
    /// </summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>
    /// Reliability = successes / (successes + failures).
    /// </summary>
    public float Reliability =>
        ValidationCount + FailureCount > 0
            ? ValidationCount / (float)(ValidationCount + FailureCount)
            : 0.5f;

    /// <summary>
    /// Context diversity: variance of conditions under which this ref was validated.
    /// High = survived across different Z regimes (robust).
    /// Low = only validated in narrow conditions (brittle).
    /// Promotion requires diversity - repetition alone is insufficient.
    /// </summary>
    public float ContextDiversity { get; private set; }

    /// <summary>
    /// Accumulated gap pressure against this reference.
    /// Sustained gap pressure can demote even Inherited refs.
    /// Inheritance is revocable trust, not permanent truth.
    /// </summary>
    public float AccumulatedGapPressure { get; private set; }

    // Track validation contexts for diversity calculation
    private readonly List<(float z0, float z1, float z4)> _validationContexts = new();
    private const int MaxContextHistory = 20;

    // ASYMMETRIC by design - demotion is faster than promotion
    private const float SuccessValidityRate = 0.15f;      // Slow promotion
    private const float FailureValidityRate = 0.28f;      // Fast demotion (1.9x faster)
    private const float SalienceDecayRate = 0.015f;       // Natural salience decay per frame
    private const float SalienceBoostOnSuccess = 0.25f;   // Boost on successful validation
    private const float SaliencePenaltyOnFailure = 0.15f; // Penalty on failure
    private const int MinValidationsForPromotion = 8;     // More evidence needed for promotion
    private const float PromotionReliability = 0.75f;     // Higher bar for promotion
    private const float DemotionReliability = 0.45f;      // Easier to demote
    private const int ConsecutiveFailureDemotion = 2;     // 2 failures = immediate demotion
    private const float MinDiversityForPromotion = 0.25f; // Must survive varied conditions
    private const float GapPressureDemotionThreshold = 0.6f; // Sustained gap pressure triggers demotion

    public ReferenceNode(RefType type, BindState initialBind = BindState.Separate)
    {
        Id = RefId.New();
        Type = type;
        TicksCreated = DateTime.UtcNow.Ticks;
        TimeCreated = DateTime.UtcNow;
        Bind = initialBind;
        Validity = 0.5f;  // Start neutral
        Salience = 1.0f;  // Start fully salient
    }

    /// <summary>
    /// Record a validation result with context (did this reference carry load?).
    /// Context is used to track diversity of survival conditions.
    /// </summary>
    public void RecordValidation(bool survived, float validityDelta, float z0 = 0f, float z1 = 0f, float z4 = 0f)
    {
        if (survived)
        {
            ValidationCount++;
            ConsecutiveFailures = 0; // Reset failure streak
            Validity = Math.Clamp(Validity + validityDelta * SuccessValidityRate, 0f, 1f);
            Salience = Math.Min(1f, Salience + SalienceBoostOnSuccess);

            // Track validation context for diversity calculation
            _validationContexts.Add((z0, z1, z4));
            if (_validationContexts.Count > MaxContextHistory)
                _validationContexts.RemoveAt(0);

            // Update context diversity (variance of Z values across validations)
            UpdateContextDiversity();

            // Gap pressure decays on successful validation
            AccumulatedGapPressure = Math.Max(0f, AccumulatedGapPressure - 0.1f);

            TryPromote();
        }
        else
        {
            FailureCount++;
            ConsecutiveFailures++;
            // FASTER demotion - reality changes faster than truth stabilizes
            Validity = Math.Clamp(Validity - validityDelta * FailureValidityRate, 0f, 1f);
            Salience = Math.Max(0.1f, Salience - SaliencePenaltyOnFailure);
            TryDemote();
        }
    }

    /// <summary>
    /// Apply gap pressure against this reference.
    /// Sustained gap pressure can demote even Inherited refs.
    /// </summary>
    public void ApplyGapPressure(float pressure)
    {
        AccumulatedGapPressure = Math.Min(1f, AccumulatedGapPressure + pressure * 0.1f);

        // Sustained gap pressure triggers demotion (even for Inherited)
        if (AccumulatedGapPressure > GapPressureDemotionThreshold)
        {
            // Inheritance is revocable trust
            if (Bind == BindState.Inherited) Bind = BindState.Associated;
            else if (Bind == BindState.Associated) Bind = BindState.Separate;

            // Reset pressure after demotion
            AccumulatedGapPressure *= 0.5f;
        }
    }

    /// <summary>
    /// Update context diversity based on validation history.
    /// </summary>
    private void UpdateContextDiversity()
    {
        if (_validationContexts.Count < 3)
        {
            ContextDiversity = 0f;
            return;
        }

        // Calculate variance for each Z dimension
        float varZ0 = CalculateVariance(_validationContexts.Select(c => c.z0));
        float varZ1 = CalculateVariance(_validationContexts.Select(c => c.z1));
        float varZ4 = CalculateVariance(_validationContexts.Select(c => c.z4));

        // Combined diversity (normalized)
        // Higher variance = more diverse conditions = more robust survival
        ContextDiversity = Math.Min(1f, (varZ0 + varZ1 + varZ4) / 3f);
    }

    private static float CalculateVariance(IEnumerable<float> values)
    {
        var list = values.ToList();
        if (list.Count < 2) return 0f;

        float mean = list.Average();
        float sumSquares = list.Sum(v => (v - mean) * (v - mean));
        return sumSquares / list.Count;
    }

    /// <summary>
    /// Apply natural salience decay (call each frame).
    /// Low-salience references are pruned first.
    /// </summary>
    public void ApplySalienceDecay()
    {
        Salience = Math.Max(0f, Salience - SalienceDecayRate);
    }

    /// <summary>
    /// Refresh salience (when reference is accessed/attended to).
    /// </summary>
    public void RefreshSalience()
    {
        Salience = Math.Min(1f, Salience + 0.1f);
    }

    /// <summary>
    /// Try to promote this reference to higher bind state.
    /// Promotion is SLOW - requires substantial evidence AND diverse survival.
    /// Repetition in same conditions is insufficient.
    /// </summary>
    private void TryPromote()
    {
        // Promotion requires substantial evidence
        if (ValidationCount < MinValidationsForPromotion) return;
        if (Reliability < PromotionReliability) return;
        if (ConsecutiveFailures > 0) return; // No promotion with recent failures

        // CRITICAL: Promotion requires diverse survival conditions
        // Surviving 10 times in identical conditions is NOT promotion-worthy
        // Must survive across different Z regimes to prove robustness
        if (ContextDiversity < MinDiversityForPromotion) return;

        // Gap pressure blocks promotion (unresolved uncertainty)
        if (AccumulatedGapPressure > 0.3f) return;

        if (Bind == BindState.Separate && Reliability > 0.65f)
        {
            Bind = BindState.Associated;
        }
        else if (Bind == BindState.Associated &&
                 Reliability > 0.85f &&
                 ValidationCount > MinValidationsForPromotion * 2 &&
                 ContextDiversity > MinDiversityForPromotion * 1.5f) // Higher diversity bar for Inherited
        {
            Bind = BindState.Inherited;
        }
    }

    /// <summary>
    /// Try to demote this reference to lower bind state.
    /// Demotion is FAST - reality changes faster than truth stabilizes.
    /// </summary>
    private void TryDemote()
    {
        // Consecutive failures trigger immediate demotion
        if (ConsecutiveFailures >= ConsecutiveFailureDemotion)
        {
            // Harsh: drop one level immediately
            if (Bind == BindState.Inherited) Bind = BindState.Associated;
            else if (Bind == BindState.Associated) Bind = BindState.Separate;
            else if (Bind == BindState.Separate) Bind = BindState.Absent;
            return;
        }

        // Standard demotion based on reliability (easier thresholds)
        if (Reliability > DemotionReliability) return;

        if (Bind == BindState.Inherited)
            Bind = BindState.Associated;
        else if (Bind == BindState.Associated && Reliability < 0.35f)
            Bind = BindState.Separate;
        else if (Bind == BindState.Separate && Reliability < 0.25f)
            Bind = BindState.Absent;
    }

    /// <summary>
    /// Add a tag to this reference.
    /// </summary>
    public void AddTag(string tag) => Tags.Add(tag);

    /// <summary>
    /// Add a metric to this reference.
    /// </summary>
    public void SetMetric(string key, float value) => Metrics[key] = value;

    /// <summary>
    /// Link this reference to another.
    /// </summary>
    public void LinkTo(RefId other)
    {
        if (!Links.Contains(other))
            Links.Add(other);
    }

    /// <summary>
    /// Age of this reference in milliseconds.
    /// </summary>
    public double AgeMs => (DateTime.UtcNow.Ticks - TicksCreated) / TimeSpan.TicksPerMillisecond;

    public override string ToString() =>
        $"{Type}[{Id}] Bind={Bind} V={Validity:F2} R={Reliability:F2} S={Salience:F2} D={ContextDiversity:F2} G={AccumulatedGapPressure:F2}";
}
