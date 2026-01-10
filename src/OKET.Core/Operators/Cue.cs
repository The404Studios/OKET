namespace OKET.Core.Operators;

/// <summary>
/// The scope of a cue - from local to global.
///
/// Cues start local and can be promoted to higher scopes
/// when they prove reliable across contexts.
/// </summary>
public enum CueScope
{
    /// <summary>
    /// Local: valid only in current immediate context.
    /// e.g., "this specific enemy pattern"
    /// </summary>
    Local,

    /// <summary>
    /// Regional: valid across similar contexts.
    /// e.g., "enemy approaching from audio cue"
    /// </summary>
    Regional,

    /// <summary>
    /// Global: valid across all contexts.
    /// e.g., "low health means retreat"
    /// </summary>
    Global
}

/// <summary>
/// A cue is a cheap predicate that predicts stability.
///
/// Cues are NOT truths. They are hypotheses that earn the right
/// to reduce pressure at the sink by repeatedly predicting
/// which postures will survive.
///
/// A cue is valid when:
/// - It fires
/// - The posture commits
/// - Strain falls
/// - Outcomes improve
///
/// Cues that lie decay naturally.
/// Cues that work harden into boundaries.
///
/// CRITICAL: Demotion is FASTER than promotion.
/// Reality changes faster than truth stabilizes.
/// This prevents boundary dogma.
/// </summary>
public sealed class Cue
{
    /// <summary>
    /// Unique identifier for this cue.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Human-readable description.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Current scope (local → regional → global).
    /// </summary>
    public CueScope Scope { get; private set; }

    /// <summary>
    /// Reliability [0, 1]. How often this cue predicts survival.
    /// </summary>
    public float Reliability { get; private set; }

    /// <summary>
    /// Expected strain reduction when this cue fires.
    /// Positive = strain expected to fall.
    /// </summary>
    public float ExpectedStrainDelta { get; private set; }

    /// <summary>
    /// Computational cost to evaluate [0, 1].
    /// Lower is better.
    /// </summary>
    public float Cost { get; }

    /// <summary>
    /// Number of times this cue has been tested.
    /// </summary>
    public int TestCount { get; private set; }

    /// <summary>
    /// Number of times this cue correctly predicted survival.
    /// </summary>
    public int SuccessCount { get; private set; }

    /// <summary>
    /// Consecutive failures (resets on success).
    /// Used for rapid demotion.
    /// </summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>
    /// Whether this cue has hardened into a boundary.
    /// Boundaries are cues with high reliability, low cost, and global scope.
    /// </summary>
    public bool IsBoundary =>
        Reliability > 0.85f &&
        Cost < 0.3f &&
        Scope == CueScope.Global &&
        TestCount > 30 &&
        ConsecutiveFailures == 0;

    /// <summary>
    /// Current binding state of this cue.
    /// </summary>
    public BindState BindState { get; private set; } = BindState.Separate;

    // Configuration - ASYMMETRIC by design
    // Promotion is slow and requires evidence
    // Demotion is fast and triggered by failure
    private const float SuccessLearningRate = 0.08f;     // Slow promotion
    private const float FailureLearningRate = 0.15f;     // Fast demotion (1.9x faster)
    private const float DecayRate = 0.02f;
    private const int MinTestsForPromotion = 15;          // More evidence needed
    private const float PromotionThreshold = 0.78f;       // Higher bar
    private const float DemotionThreshold = 0.5f;         // Easier to demote (was 0.4)
    private const int ConsecutiveFailureDemotion = 3;     // 3 failures = immediate demotion

    public Cue(string id, string description, float cost = 0.1f)
    {
        Id = id;
        Description = description;
        Cost = Math.Clamp(cost, 0f, 1f);
        Reliability = 0.5f; // Start neutral
        ExpectedStrainDelta = 0f;
        Scope = CueScope.Local;
    }

    /// <summary>
    /// Record a test result for this cue.
    /// </summary>
    /// <param name="fired">Did the cue fire?</param>
    /// <param name="survived">Did the posture survive the sink?</param>
    /// <param name="strainDelta">Actual change in strain (negative = improved).</param>
    /// <param name="outcomeDelta">Change in outcomes.</param>
    public void RecordResult(bool fired, bool survived, float strainDelta, float outcomeDelta)
    {
        if (!fired) return; // Only update when cue actually fired

        TestCount++;

        if (survived && outcomeDelta >= 0)
        {
            // Success: cue predicted survival
            SuccessCount++;
            ConsecutiveFailures = 0; // Reset failure streak
            Reliability = Reliability + SuccessLearningRate * (1f - Reliability);
            ExpectedStrainDelta = ExpectedStrainDelta * 0.9f + strainDelta * 0.1f;

            // Consider promotion (slow, requires evidence)
            TryPromote();
        }
        else
        {
            // Failure: cue did not predict survival
            ConsecutiveFailures++;
            Reliability = Reliability - FailureLearningRate * Reliability;
            ExpectedStrainDelta = ExpectedStrainDelta * 0.8f; // Faster decay on failure

            // Consider demotion (fast, aggressive)
            TryDemote();
        }
    }

    /// <summary>
    /// Apply natural decay (called each frame regardless of firing).
    /// </summary>
    public void ApplyDecay()
    {
        // Slight decay toward neutral
        Reliability = Reliability * (1f - DecayRate * 0.1f) + 0.5f * DecayRate * 0.1f;
    }

    private void TryPromote()
    {
        // Promotion requires substantial evidence
        if (TestCount < MinTestsForPromotion) return;
        if (Reliability < PromotionThreshold) return;
        if (ConsecutiveFailures > 0) return; // No promotion with recent failures

        // Promote scope (requires more tests at each level)
        if (Scope == CueScope.Local && TestCount > MinTestsForPromotion)
        {
            Scope = CueScope.Regional;
        }
        else if (Scope == CueScope.Regional && TestCount > MinTestsForPromotion * 3)
        {
            Scope = CueScope.Global;
        }

        // Promote bind state (conservative)
        if (BindState == BindState.Separate && Reliability > 0.65f && TestCount > MinTestsForPromotion)
        {
            BindState = BindState.Associated;
        }
        else if (BindState == BindState.Associated &&
                 Reliability > 0.85f &&
                 Scope == CueScope.Global &&
                 TestCount > MinTestsForPromotion * 2)
        {
            BindState = BindState.Inherited;
        }
    }

    private void TryDemote()
    {
        // FAST demotion - reality changes faster than truth stabilizes

        // Consecutive failures trigger immediate demotion
        if (ConsecutiveFailures >= ConsecutiveFailureDemotion)
        {
            // Harsh: drop one level in both scope and bind
            if (Scope == CueScope.Global) Scope = CueScope.Regional;
            else if (Scope == CueScope.Regional) Scope = CueScope.Local;

            if (BindState == BindState.Inherited) BindState = BindState.Associated;
            else if (BindState == BindState.Associated) BindState = BindState.Separate;

            return;
        }

        // Standard demotion based on reliability
        if (Reliability > DemotionThreshold) return;

        // Demote scope
        if (Scope == CueScope.Global)
        {
            Scope = CueScope.Regional;
        }
        else if (Scope == CueScope.Regional)
        {
            Scope = CueScope.Local;
        }

        // Demote bind state (more aggressive thresholds)
        if (BindState == BindState.Inherited && Reliability < 0.6f)
        {
            BindState = BindState.Associated;
        }
        else if (BindState == BindState.Associated && Reliability < 0.4f)
        {
            BindState = BindState.Separate;
        }
    }

    /// <summary>
    /// Get the strain discount this cue provides when firing.
    /// Reliable cues reduce effective strain.
    /// </summary>
    public float GetStrainDiscount()
    {
        if (Reliability < 0.6f) return 0f;

        // Scale by reliability and scope
        float scopeMultiplier = Scope switch
        {
            CueScope.Local => 0.5f,
            CueScope.Regional => 0.75f,
            CueScope.Global => 1.0f,
            _ => 0f
        };

        return Math.Max(0, -ExpectedStrainDelta) * Reliability * scopeMultiplier * 0.3f;
    }

    public override string ToString() =>
        $"Cue[{Id}] R={Reliability:F2} S={Scope} B={BindState} ({SuccessCount}/{TestCount})";
}
