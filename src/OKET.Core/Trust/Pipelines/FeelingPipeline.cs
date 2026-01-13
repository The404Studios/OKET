namespace OKET.Core.Trust.Pipelines;

/// <summary>
/// Feeling Pipeline - Affect as State Estimation.
///
/// Feelings are state estimators, not just reactions.
///
/// RAW AFFECT:
///   Input: autonomic data + interoceptive signals + context + memory echoes
///   Transform: affect gradients (arousal/valence/tension/relief)
///   Output: AffectAtoms
///
/// TRUSTED FEELING:
///   Gates (CAs):
///     CA-F0 Body Plausibility: matches physiology?
///     CA-F1 Context Fit: external correlate or free-floating?
///     CA-F2 Temporal Stability: persists appropriately?
///     CA-F3 Outcome Validity: acting on it improves outcomes?
///
///   Sinks:
///     - Chemical noise sink (caffeine/adrenaline/withdrawal)
///     - Projection sink (old memory echo)
///     - Panic sink (high arousal, low context)
///
///   Output: CertifiedFeeling with authority score
///
/// RULE: Trusted feeling is not "strong feeling." It's certified feeling.
/// </summary>
public sealed class FeelingPipeline
{
    // Certification authorities
    private readonly BodyPlausibilityCA _caBodyPlausibility = new();
    private readonly ContextFitCA _caContextFit = new();
    private readonly TemporalStabilityCA _caTemporalStability = new();
    private readonly OutcomeValidityCA _caOutcomeValidity = new();

    // Sinks
    private readonly ChemicalNoiseSink _chemicalSink = new();
    private readonly ProjectionSink _projectionSink = new();
    private readonly PanicSink _panicSink = new();

    // State tracking
    private readonly Queue<AffectAtom> _recentAffects = new();
    private readonly Dictionary<FeelingType, FeelingHistory> _history = new();
    private const int MaxHistory = 100;

    // Current certified state
    private CertifiedFeeling? _current;
    private GateState _gateState = GateState.Probe;

    // Thresholds
    private readonly GateThresholds _thresholds;

    // Statistics
    private int _totalProcessed;
    private int _certified;
    private int _sunk;

    public CertifiedFeeling? Current => _current;
    public GateState CurrentGateState => _gateState;
    public float CertificationRate => _totalProcessed > 0 ? (float)_certified / _totalProcessed : 0;

    public FeelingPipeline()
    {
        _thresholds = new GateThresholds
        {
            AllowThreshold = 0.65f,
            ProbeThreshold = 0.35f,
            MaxRisk = 0.85f,
            MinCoherence = 0.25f,
            Hysteresis = 0.05f,
            Weights = new DimensionalWeights
            {
                Coherence = 0.20f,
                Stability = 0.25f,
                ContextFit = 0.25f,
                Risk = 0.10f,
                Reversibility = 0.05f,
                OutcomeHistory = 0.15f,
                Novelty = 0f
            }.Normalized()
        };
    }

    /// <summary>
    /// Process raw affect into (potentially) certified feeling.
    /// </summary>
    public FeelingResult Process(AffectInput input)
    {
        _totalProcessed++;

        // === STAGE 1: TRANSFORM TO AFFECT ATOMS ===
        var affectAtom = TransformToAffect(input);
        RecordAffect(affectAtom);

        // === STAGE 2: COMPUTE DIMENSIONAL SCORES ===
        var scores = ComputeScores(affectAtom, input);

        // === STAGE 3: RUN CERTIFICATION AUTHORITIES ===
        var caResults = new List<CertificationResult>
        {
            _caBodyPlausibility.Certify(affectAtom, input, scores),
            _caContextFit.Certify(affectAtom, input, scores),
            _caTemporalStability.Certify(affectAtom, _recentAffects, scores),
            _caOutcomeValidity.Certify(affectAtom, GetHistory(affectAtom.Type), scores)
        };

        // Aggregate CA results
        scores = AggregateCaResults(caResults, scores);

        // === STAGE 4: GATE DECISION ===
        var decision = CognitiveGate.Evaluate(scores, _thresholds, _gateState);
        _gateState = decision.State;

        // === STAGE 5: SINK OR PROMOTE ===
        if (decision.ShouldSink)
        {
            _sunk++;
            RoutToSinks(affectAtom, decision);

            return new FeelingResult
            {
                RawAffect = affectAtom,
                GateDecision = decision,
                IsCertified = false,
                CertifiedFeeling = null,
                SunkTo = GetActiveSink(affectAtom, decision)
            };
        }

        if (decision.ShouldProbe)
        {
            // Probe - don't fully certify yet
            var probe = GenerateProbe(affectAtom, scores);

            return new FeelingResult
            {
                RawAffect = affectAtom,
                GateDecision = decision,
                IsCertified = false,
                CertifiedFeeling = null,
                ProbeAction = probe
            };
        }

        // === STAGE 6: CERTIFY ===
        _certified++;
        var certified = Certify(affectAtom, scores, caResults);
        _current = certified;

        return new FeelingResult
        {
            RawAffect = affectAtom,
            GateDecision = decision,
            IsCertified = true,
            CertifiedFeeling = certified
        };
    }

    /// <summary>
    /// Transform raw input to affect atom.
    /// </summary>
    private static AffectAtom TransformToAffect(AffectInput input)
    {
        // Compute affect gradients
        float arousal = ComputeArousal(input);
        float valence = ComputeValence(input);
        float tension = ComputeTension(input);
        float relief = ComputeRelief(input);

        // Determine dominant feeling type
        var type = ClassifyFeeling(arousal, valence, tension, relief);

        return new AffectAtom
        {
            Type = type,
            Arousal = arousal,
            Valence = valence,
            Tension = tension,
            Relief = relief,
            Intensity = ComputeIntensity(arousal, tension),
            Source = input.PrimarySource,
            Timestamp = DateTime.UtcNow
        };
    }

    private static float ComputeArousal(AffectInput input)
    {
        // Arousal from: threat, movement, strain, health changes
        float threatArousal = input.ThreatLevel * 0.8f;
        float strainArousal = input.SystemStrain * 0.3f;
        float healthArousal = input.HealthChange < 0 ? Math.Abs(input.HealthChange) * 2f : 0;
        float noveltyArousal = input.NoveltyLevel * 0.4f;

        return Math.Clamp(threatArousal + strainArousal + healthArousal + noveltyArousal, 0f, 1f);
    }

    private static float ComputeValence(AffectInput input)
    {
        // Valence: positive (opportunity, success) vs negative (threat, failure)
        float positive = input.OpportunityLevel * 0.6f +
                        (input.RecentSuccess ? 0.3f : 0f) +
                        (input.HealthChange > 0 ? input.HealthChange : 0);

        float negative = input.ThreatLevel * 0.6f +
                        (input.RecentFailure ? 0.3f : 0f) +
                        (input.HealthChange < 0 ? Math.Abs(input.HealthChange) : 0);

        return Math.Clamp((positive - negative + 1f) / 2f, 0f, 1f); // 0.5 = neutral
    }

    private static float ComputeTension(AffectInput input)
    {
        // Tension: unresolved pressure, need to act
        float conflictTension = input.ConflictingSignals * 0.5f;
        float timePressure = input.Urgency * 0.4f;
        float resourceTension = (1f - input.ResourceStatus) * 0.3f;

        return Math.Clamp(conflictTension + timePressure + resourceTension, 0f, 1f);
    }

    private static float ComputeRelief(AffectInput input)
    {
        // Relief: tension reduction, safety, success
        float safetyRelief = input.ThreatLevel < 0.2f ? 0.3f : 0f;
        float successRelief = input.RecentSuccess ? 0.4f : 0f;
        float resourceRelief = input.ResourceStatus > 0.7f ? 0.2f : 0f;

        return Math.Clamp(safetyRelief + successRelief + resourceRelief, 0f, 1f);
    }

    private static FeelingType ClassifyFeeling(float arousal, float valence, float tension, float relief)
    {
        // High arousal, low valence = fear/anger
        if (arousal > 0.6f && valence < 0.4f)
            return tension > relief ? FeelingType.Fear : FeelingType.Anger;

        // High arousal, high valence = excitement
        if (arousal > 0.6f && valence > 0.6f)
            return FeelingType.Excitement;

        // Low arousal, low valence = sadness/frustration
        if (arousal < 0.4f && valence < 0.4f)
            return FeelingType.Frustration;

        // Low arousal, high valence = calm/content
        if (arousal < 0.4f && valence > 0.6f)
            return FeelingType.Calm;

        // High tension = anxiety
        if (tension > 0.6f)
            return FeelingType.Anxiety;

        // High relief = relief
        if (relief > 0.6f)
            return FeelingType.Relief;

        // Mixed = uncertain
        return FeelingType.Uncertain;
    }

    private static float ComputeIntensity(float arousal, float tension)
    {
        return Math.Clamp(arousal * 0.6f + tension * 0.4f, 0f, 1f);
    }

    /// <summary>
    /// Compute dimensional scores for the affect.
    /// </summary>
    private DimensionalScores ComputeScores(AffectAtom affect, AffectInput input)
    {
        return new DimensionalScores
        {
            Coherence = ComputeAffectCoherence(affect),
            Stability = ComputeAffectStability(affect),
            ContextFit = ComputeContextFit(affect, input),
            Risk = affect.Type.IsNegative() ? affect.Intensity * 0.5f : 0.1f,
            Reversibility = 0.9f, // Feelings are reversible
            OutcomeHistory = GetOutcomeHistory(affect.Type),
            Novelty = affect.Source == AffectSource.Novel ? 0.7f : 0.2f
        };
    }

    private float ComputeAffectCoherence(AffectAtom affect)
    {
        // Check if affect components are internally consistent
        // High arousal + high relief is incoherent
        float arousalReliefConflict = Math.Max(0, affect.Arousal + affect.Relief - 1f);

        // Tension + calm is incoherent
        bool tensionCalmConflict = affect.Tension > 0.5f && affect.Type == FeelingType.Calm;

        float coherence = 1f - arousalReliefConflict * 0.5f - (tensionCalmConflict ? 0.3f : 0);
        return Math.Clamp(coherence, 0f, 1f);
    }

    private float ComputeAffectStability(AffectAtom current)
    {
        if (_recentAffects.Count < 3) return 0.5f;

        // Check how much feeling has changed recently
        var recent = _recentAffects.TakeLast(5).ToList();
        float typeChanges = 0;
        var prev = recent[0].Type;
        foreach (var a in recent.Skip(1))
        {
            if (a.Type != prev) typeChanges++;
            prev = a.Type;
        }

        float intensityVar = recent.Select(a => a.Intensity).Max() -
                            recent.Select(a => a.Intensity).Min();

        return 1f - (typeChanges / 4f) - intensityVar * 0.3f;
    }

    private static float ComputeContextFit(AffectAtom affect, AffectInput input)
    {
        // Fear should correlate with threat
        if (affect.Type == FeelingType.Fear)
            return input.ThreatLevel > 0.3f ? 0.8f : 0.3f;

        // Excitement should correlate with opportunity or success
        if (affect.Type == FeelingType.Excitement)
            return (input.OpportunityLevel > 0.3f || input.RecentSuccess) ? 0.8f : 0.3f;

        // Calm should correlate with low threat
        if (affect.Type == FeelingType.Calm)
            return input.ThreatLevel < 0.2f ? 0.8f : 0.3f;

        // Anxiety without context is free-floating
        if (affect.Type == FeelingType.Anxiety)
            return input.ConflictingSignals > 0.3f ? 0.7f : 0.4f;

        return 0.5f; // Neutral
    }

    private float GetOutcomeHistory(FeelingType type)
    {
        if (_history.TryGetValue(type, out var history))
            return history.SuccessRate;
        return 0.5f;
    }

    /// <summary>
    /// Aggregate CA results into scores.
    /// </summary>
    private static DimensionalScores AggregateCaResults(
        List<CertificationResult> results,
        DimensionalScores baseScores)
    {
        float passCount = results.Count(r => r.Passed);
        float avgScore = results.Average(r => r.Score);

        var scores = baseScores;
        scores.Coherence = (scores.Coherence + avgScore) / 2f;
        scores.Stability *= passCount / results.Count;

        return scores;
    }

    /// <summary>
    /// Route denied affect to appropriate sink.
    /// </summary>
    private void RoutToSinks(AffectAtom affect, GateDecision decision)
    {
        if (_chemicalSink.ShouldCapture(affect, decision))
            _chemicalSink.Capture(affect, decision);
        else if (_projectionSink.ShouldCapture(affect, decision))
            _projectionSink.Capture(affect, decision);
        else if (_panicSink.ShouldCapture(affect, decision))
            _panicSink.Capture(affect, decision);
    }

    private string? GetActiveSink(AffectAtom affect, GateDecision decision)
    {
        if (_chemicalSink.ShouldCapture(affect, decision)) return _chemicalSink.Name;
        if (_projectionSink.ShouldCapture(affect, decision)) return _projectionSink.Name;
        if (_panicSink.ShouldCapture(affect, decision)) return _panicSink.Name;
        return null;
    }

    /// <summary>
    /// Generate probe action for uncertain feelings.
    /// </summary>
    private static ProbeAction GenerateProbe(AffectAtom affect, DimensionalScores scores)
    {
        // For feelings, probing means:
        // - Wait and observe if context changes
        // - Check body signals more carefully
        // - Don't act on the feeling yet

        return new ProbeAction
        {
            Type = ProbeType.Wait,
            Target = affect,
            SafetyMargin = 0.9f,
            Duration = TimeSpan.FromMilliseconds(500),
            Reasoning = $"Probe feeling: {affect.Type} (stability={scores.Stability:F2})"
        };
    }

    /// <summary>
    /// Certify a feeling.
    /// </summary>
    private static CertifiedFeeling Certify(
        AffectAtom affect,
        DimensionalScores scores,
        List<CertificationResult> caResults)
    {
        return new CertifiedFeeling
        {
            Type = affect.Type,
            Intensity = affect.Intensity,
            Arousal = affect.Arousal,
            Valence = affect.Valence,
            AuthorityScore = scores.Coherence * scores.Stability * scores.ContextFit,
            Scores = scores,
            CertificationChain = caResults.Where(r => r.Passed).Select(r => r.Reason).ToList(),
            CertifiedAt = DateTime.UtcNow,
            Source = affect.Source
        };
    }

    /// <summary>
    /// Record affect for history.
    /// </summary>
    private void RecordAffect(AffectAtom affect)
    {
        _recentAffects.Enqueue(affect);
        while (_recentAffects.Count > 30)
            _recentAffects.Dequeue();
    }

    private FeelingHistory GetHistory(FeelingType type)
    {
        if (!_history.TryGetValue(type, out var history))
        {
            history = new FeelingHistory();
            _history[type] = history;
        }
        return history;
    }

    /// <summary>
    /// Record outcome of acting on a feeling.
    /// </summary>
    public void RecordOutcome(FeelingType type, bool success)
    {
        var history = GetHistory(type);
        history.Record(success);
    }

    /// <summary>
    /// Decay uncertified state.
    /// </summary>
    public void Decay()
    {
        if (_current != null)
        {
            _current.FramesSinceCertified++;
            if (_current.FramesSinceCertified > 60) // ~2 seconds
            {
                _current = null;
                _gateState = GateState.Probe;
            }
        }
    }

    public string GetDiagnostics()
    {
        return $"""
            === FEELING PIPELINE ===
            Processed: {_totalProcessed}
            Certified: {_certified} ({CertificationRate:P0})
            Sunk: {_sunk}
            Current: {_current?.Type.ToString() ?? "none"} (intensity={_current?.Intensity:F2})
            Gate State: {_gateState}
            Sinks: chemical={_chemicalSink.CapturedCount}, projection={_projectionSink.CapturedCount}, panic={_panicSink.CapturedCount}
            ========================
            """;
    }
}

// ============== TYPES ==============

/// <summary>
/// Input for feeling pipeline.
/// </summary>
public readonly struct AffectInput
{
    public float ThreatLevel { get; init; }
    public float OpportunityLevel { get; init; }
    public float SystemStrain { get; init; }
    public float HealthChange { get; init; }
    public float ResourceStatus { get; init; }
    public float NoveltyLevel { get; init; }
    public float ConflictingSignals { get; init; }
    public float Urgency { get; init; }
    public bool RecentSuccess { get; init; }
    public bool RecentFailure { get; init; }
    public AffectSource PrimarySource { get; init; }
}

/// <summary>
/// Source of affect.
/// </summary>
public enum AffectSource
{
    Perception,    // From current perception
    Interoception, // From body signals
    Memory,        // From memory echo
    Prediction,    // From prediction/expectation
    Novel          // From novel situation
}

/// <summary>
/// Raw affect atom (uncertified).
/// </summary>
public readonly struct AffectAtom
{
    public FeelingType Type { get; init; }
    public float Arousal { get; init; }
    public float Valence { get; init; }
    public float Tension { get; init; }
    public float Relief { get; init; }
    public float Intensity { get; init; }
    public AffectSource Source { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Types of feelings.
/// </summary>
public enum FeelingType
{
    Fear,
    Anger,
    Excitement,
    Frustration,
    Calm,
    Anxiety,
    Relief,
    Uncertain
}

public static class FeelingTypeExtensions
{
    public static bool IsNegative(this FeelingType type) =>
        type is FeelingType.Fear or FeelingType.Anger or
                FeelingType.Frustration or FeelingType.Anxiety;

    public static bool IsPositive(this FeelingType type) =>
        type is FeelingType.Excitement or FeelingType.Calm or FeelingType.Relief;
}

/// <summary>
/// Certified feeling with authority chain.
/// </summary>
public sealed class CertifiedFeeling
{
    public FeelingType Type { get; init; }
    public float Intensity { get; init; }
    public float Arousal { get; init; }
    public float Valence { get; init; }
    public float AuthorityScore { get; init; }
    public DimensionalScores Scores { get; init; }
    public List<string> CertificationChain { get; init; } = new();
    public DateTime CertifiedAt { get; init; }
    public AffectSource Source { get; init; }
    public int FramesSinceCertified { get; set; }
}

/// <summary>
/// Result of feeling pipeline.
/// </summary>
public readonly struct FeelingResult
{
    public AffectAtom RawAffect { get; init; }
    public GateDecision GateDecision { get; init; }
    public bool IsCertified { get; init; }
    public CertifiedFeeling? CertifiedFeeling { get; init; }
    public string? SunkTo { get; init; }
    public ProbeAction? ProbeAction { get; init; }
}

/// <summary>
/// History of feeling outcomes.
/// </summary>
internal sealed class FeelingHistory
{
    private int _successes;
    private int _total;

    public float SuccessRate => _total > 0 ? (float)_successes / _total : 0.5f;

    public void Record(bool success)
    {
        _total++;
        if (success) _successes++;
    }
}

// ============== CERTIFICATION AUTHORITIES ==============

/// <summary>
/// CA-F0: Body Plausibility - does it match physiology?
/// </summary>
internal sealed class BodyPlausibilityCA
{
    public CertificationResult Certify(AffectAtom affect, AffectInput input, DimensionalScores scores)
    {
        // High arousal needs physiological trigger
        if (affect.Arousal > 0.7f)
        {
            bool hasTrigger = input.ThreatLevel > 0.3f ||
                             input.HealthChange < -0.1f ||
                             input.NoveltyLevel > 0.5f;

            if (!hasTrigger)
                return CertificationResult.Fail("High arousal without physiological trigger", scores);
        }

        // Calm inconsistent with high threat
        if (affect.Type == FeelingType.Calm && input.ThreatLevel > 0.5f)
            return CertificationResult.Fail("Calm under high threat implausible", scores);

        return CertificationResult.Pass(0.8f, scores, "Body plausible");
    }
}

/// <summary>
/// CA-F1: Context Fit - external correlate or free-floating?
/// </summary>
internal sealed class ContextFitCA
{
    public CertificationResult Certify(AffectAtom affect, AffectInput input, DimensionalScores scores)
    {
        float contextScore = 0.5f;

        // Fear should correlate with threat
        if (affect.Type == FeelingType.Fear)
            contextScore = input.ThreatLevel > 0.2f ? 0.9f : 0.3f;

        // Excitement should correlate with opportunity
        if (affect.Type == FeelingType.Excitement)
            contextScore = input.OpportunityLevel > 0.2f ? 0.9f : 0.4f;

        // Free-floating anxiety (no clear cause)
        if (affect.Type == FeelingType.Anxiety &&
            input.ThreatLevel < 0.2f && input.ConflictingSignals < 0.3f)
            contextScore = 0.3f;

        if (contextScore < 0.4f)
            return CertificationResult.Fail("Free-floating affect (no context)", scores);

        var updated = scores;
        updated.ContextFit = contextScore;
        return CertificationResult.Pass(contextScore, updated, "Context fit");
    }
}

/// <summary>
/// CA-F2: Temporal Stability - persists appropriately?
/// </summary>
internal sealed class TemporalStabilityCA
{
    public CertificationResult Certify(AffectAtom affect, Queue<AffectAtom> history, DimensionalScores scores)
    {
        if (history.Count < 3)
            return CertificationResult.Pass(0.5f, scores, "Insufficient history");

        var recent = history.TakeLast(5).ToList();

        // Count type matches
        int matches = recent.Count(a => a.Type == affect.Type);
        float stability = matches / (float)recent.Count;

        // Check for 2-second spikes (shouldn't certify)
        if (matches < 2 && affect.Intensity > 0.7f)
            return CertificationResult.Fail("Intensity spike, not stable", scores);

        if (stability < 0.4f)
            return CertificationResult.Fail($"Unstable feeling ({stability:F2})", scores);

        var updated = scores;
        updated.Stability = stability;
        return CertificationResult.Pass(stability, updated, $"Temporal stability {stability:F2}");
    }
}

/// <summary>
/// CA-F3: Outcome Validity - acting on it improves outcomes?
/// </summary>
internal sealed class OutcomeValidityCA
{
    public CertificationResult Certify(AffectAtom affect, FeelingHistory history, DimensionalScores scores)
    {
        float successRate = history.SuccessRate;

        if (successRate < 0.3f)
            return CertificationResult.Fail($"Poor outcome history ({successRate:F2})", scores);

        var updated = scores;
        updated.OutcomeHistory = successRate;
        return CertificationResult.Pass(successRate, updated, $"Outcome validity {successRate:F2}");
    }
}

// ============== SINKS ==============

/// <summary>
/// Chemical Noise Sink - caffeine, adrenaline, withdrawal.
/// </summary>
internal sealed class ChemicalNoiseSink : BaseSink
{
    public override string Name => "ChemicalNoise";

    public override bool ShouldCapture(object claim, GateDecision decision)
    {
        if (claim is not AffectAtom affect) return false;

        // High arousal, high intensity, low context = chemical noise
        return affect.Arousal > 0.7f &&
               affect.Intensity > 0.6f &&
               decision.Scores.ContextFit < 0.4f;
    }
}

/// <summary>
/// Projection Sink - old memory echoes.
/// </summary>
internal sealed class ProjectionSink : BaseSink
{
    public override string Name => "Projection";

    public override bool ShouldCapture(object claim, GateDecision decision)
    {
        if (claim is not AffectAtom affect) return false;

        // Memory-sourced affect with poor context fit
        return affect.Source == AffectSource.Memory &&
               decision.Scores.ContextFit < 0.5f;
    }
}

/// <summary>
/// Panic Sink - high arousal, low context.
/// </summary>
internal sealed class PanicSink : BaseSink
{
    public override string Name => "Panic";

    public override bool ShouldCapture(object claim, GateDecision decision)
    {
        if (claim is not AffectAtom affect) return false;

        // Panic: very high arousal, very low context fit
        return affect.Arousal > 0.8f &&
               affect.Type == FeelingType.Fear &&
               decision.Scores.ContextFit < 0.3f &&
               decision.Scores.Stability < 0.4f;
    }
}
