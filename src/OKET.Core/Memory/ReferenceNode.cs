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
    /// Reliability = successes / (successes + failures).
    /// </summary>
    public float Reliability =>
        ValidationCount + FailureCount > 0
            ? ValidationCount / (float)(ValidationCount + FailureCount)
            : 0.5f;

    public ReferenceNode(RefType type, BindState initialBind = BindState.Separate)
    {
        Id = RefId.New();
        Type = type;
        TicksCreated = DateTime.UtcNow.Ticks;
        TimeCreated = DateTime.UtcNow;
        Bind = initialBind;
        Validity = 0.5f; // Start neutral
    }

    /// <summary>
    /// Record a validation result (did this reference carry load?).
    /// </summary>
    public void RecordValidation(bool survived, float validityDelta)
    {
        if (survived)
        {
            ValidationCount++;
            Validity = Math.Clamp(Validity + validityDelta * 0.2f, 0f, 1f);
            TryPromote();
        }
        else
        {
            FailureCount++;
            Validity = Math.Clamp(Validity - validityDelta * 0.3f, 0f, 1f);
            TryDemote();
        }
    }

    /// <summary>
    /// Try to promote this reference to higher bind state.
    /// </summary>
    private void TryPromote()
    {
        if (ValidationCount < 5) return;
        if (Reliability < 0.7f) return;

        if (Bind == BindState.Separate && Reliability > 0.6f)
            Bind = BindState.Associated;
        else if (Bind == BindState.Associated && Reliability > 0.8f && ValidationCount > 10)
            Bind = BindState.Inherited;
    }

    /// <summary>
    /// Try to demote this reference to lower bind state.
    /// </summary>
    private void TryDemote()
    {
        if (Reliability > 0.4f) return;

        if (Bind == BindState.Inherited)
            Bind = BindState.Associated;
        else if (Bind == BindState.Associated && Reliability < 0.3f)
            Bind = BindState.Separate;
        else if (Bind == BindState.Separate && Reliability < 0.2f)
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
        $"{Type}[{Id}] Bind={Bind} V={Validity:F2} R={Reliability:F2} Tags=[{string.Join(",", Tags)}]";
}
