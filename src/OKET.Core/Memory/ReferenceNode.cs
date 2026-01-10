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
/// CRITICAL: Demotion is FASTER than promotion.
/// Reality changes faster than truth stabilizes.
/// This prevents reference dogma.
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
    /// Record a validation result (did this reference carry load?).
    /// </summary>
    public void RecordValidation(bool survived, float validityDelta)
    {
        if (survived)
        {
            ValidationCount++;
            ConsecutiveFailures = 0; // Reset failure streak
            Validity = Math.Clamp(Validity + validityDelta * SuccessValidityRate, 0f, 1f);
            Salience = Math.Min(1f, Salience + SalienceBoostOnSuccess);
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
    /// Promotion is SLOW - requires substantial evidence.
    /// </summary>
    private void TryPromote()
    {
        // Promotion requires substantial evidence
        if (ValidationCount < MinValidationsForPromotion) return;
        if (Reliability < PromotionReliability) return;
        if (ConsecutiveFailures > 0) return; // No promotion with recent failures

        if (Bind == BindState.Separate && Reliability > 0.65f)
        {
            Bind = BindState.Associated;
        }
        else if (Bind == BindState.Associated &&
                 Reliability > 0.85f &&
                 ValidationCount > MinValidationsForPromotion * 2)
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
        $"{Type}[{Id}] Bind={Bind} V={Validity:F2} R={Reliability:F2} S={Salience:F2} Tags=[{string.Join(",", Tags)}]";
}
