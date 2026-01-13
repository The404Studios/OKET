namespace OKET.Core.Trust.Pipelines;

/// <summary>
/// Core Gate Pattern - The Minimal Instruction Engine.
///
/// Every pipeline stage uses the same 3-state gate:
///
///   ALLOW  → Promote upward (certified)
///   PROBE  → Gather more evidence safely
///   DENY   → Sink + optionally learn negative prototype
///
/// This is the fundamental operation that makes the system self-operating.
///
/// UNIFIED PATTERN (applies to every pipeline):
///   Raw signal → Form → Claim → Certification → State → Authorized Output → Memory
///
/// With decay for anything not continuously re-certified.
/// </summary>
public static class CognitiveGate
{
    /// <summary>
    /// Evaluate gate decision based on dimensional scores.
    /// </summary>
    public static GateDecision Evaluate(
        DimensionalScores scores,
        GateThresholds thresholds,
        GateState previousState)
    {
        // Compute composite score
        float composite = ComputeCompositeScore(scores, thresholds.Weights);

        // Apply hysteresis (avoid flicker)
        float effectiveThreshold = previousState == GateState.Allow
            ? thresholds.AllowThreshold - thresholds.Hysteresis
            : thresholds.AllowThreshold;

        float effectiveProbeThreshold = previousState == GateState.Probe
            ? thresholds.ProbeThreshold - thresholds.Hysteresis * 0.5f
            : thresholds.ProbeThreshold;

        // Determine state
        GateState newState;
        if (composite >= effectiveThreshold)
            newState = GateState.Allow;
        else if (composite >= effectiveProbeThreshold)
            newState = GateState.Probe;
        else
            newState = GateState.Deny;

        // Check hard constraints (instant deny)
        if (scores.Risk > thresholds.MaxRisk)
            newState = GateState.Deny;

        if (scores.Coherence < thresholds.MinCoherence)
            newState = GateState.Deny;

        return new GateDecision
        {
            State = newState,
            CompositeScore = composite,
            Scores = scores,
            Confidence = ComputeConfidence(scores, newState),
            ShouldProbe = newState == GateState.Probe,
            ShouldSink = newState == GateState.Deny,
            ShouldPromote = newState == GateState.Allow,
            Reason = GenerateReason(scores, newState, thresholds)
        };
    }

    /// <summary>
    /// Compute composite score from dimensional scores.
    /// </summary>
    private static float ComputeCompositeScore(
        DimensionalScores scores,
        DimensionalWeights weights)
    {
        return scores.Coherence * weights.Coherence +
               scores.Stability * weights.Stability +
               scores.ContextFit * weights.ContextFit +
               (1f - scores.Risk) * weights.Risk +
               scores.Reversibility * weights.Reversibility +
               scores.OutcomeHistory * weights.OutcomeHistory +
               scores.Novelty * weights.Novelty;
    }

    /// <summary>
    /// Compute confidence in gate decision.
    /// </summary>
    private static float ComputeConfidence(DimensionalScores scores, GateState state)
    {
        float baseConf = state switch
        {
            GateState.Allow => 0.7f + scores.Coherence * 0.3f,
            GateState.Probe => 0.4f + scores.Stability * 0.3f,
            GateState.Deny => 0.8f, // High confidence in denial
            _ => 0.5f
        };

        return Math.Clamp(baseConf * scores.Stability, 0f, 1f);
    }

    /// <summary>
    /// Generate human-readable reason for decision.
    /// </summary>
    private static string GenerateReason(
        DimensionalScores scores,
        GateState state,
        GateThresholds thresholds)
    {
        return state switch
        {
            GateState.Allow => $"Certified (coherence={scores.Coherence:F2}, stability={scores.Stability:F2})",
            GateState.Probe => GetProbeReason(scores, thresholds),
            GateState.Deny => GetDenyReason(scores, thresholds),
            _ => "Unknown"
        };
    }

    private static string GetProbeReason(DimensionalScores scores, GateThresholds thresholds)
    {
        if (scores.Coherence < thresholds.MinCoherence + 0.2f)
            return $"Probe: low coherence ({scores.Coherence:F2})";
        if (scores.Stability < 0.5f)
            return $"Probe: unstable ({scores.Stability:F2})";
        if (scores.ContextFit < 0.5f)
            return $"Probe: context uncertain ({scores.ContextFit:F2})";
        return "Probe: gathering evidence";
    }

    private static string GetDenyReason(DimensionalScores scores, GateThresholds thresholds)
    {
        if (scores.Risk > thresholds.MaxRisk)
            return $"Deny: risk too high ({scores.Risk:F2})";
        if (scores.Coherence < thresholds.MinCoherence)
            return $"Deny: incoherent ({scores.Coherence:F2})";
        if (scores.Stability < 0.2f)
            return $"Deny: too unstable ({scores.Stability:F2})";
        return "Deny: below threshold";
    }
}

/// <summary>
/// Gate states - the minimal instruction set.
/// </summary>
public enum GateState
{
    /// <summary>Promote upward (certified).</summary>
    Allow,

    /// <summary>Gather more evidence safely.</summary>
    Probe,

    /// <summary>Sink + optionally learn negative prototype.</summary>
    Deny
}

/// <summary>
/// Result of gate evaluation.
/// </summary>
public readonly struct GateDecision
{
    public GateState State { get; init; }
    public float CompositeScore { get; init; }
    public DimensionalScores Scores { get; init; }
    public float Confidence { get; init; }
    public bool ShouldProbe { get; init; }
    public bool ShouldSink { get; init; }
    public bool ShouldPromote { get; init; }
    public string Reason { get; init; }
}

/// <summary>
/// Unified dimensional state vector - shared across all pipelines.
///
/// These dimensions allow every pipeline to:
/// 1. Update dimensions
/// 2. Gate based on thresholds
/// 3. Promote or sink
/// </summary>
public struct DimensionalScores
{
    /// <summary>Internal consistency, lack of contradictions.</summary>
    public float Coherence { get; set; }

    /// <summary>Persistence over time, not flickering.</summary>
    public float Stability { get; set; }

    /// <summary>Match with external correlates/context.</summary>
    public float ContextFit { get; set; }

    /// <summary>Potential for harm (inverted for scoring).</summary>
    public float Risk { get; set; }

    /// <summary>Can this be undone if wrong?</summary>
    public float Reversibility { get; set; }

    /// <summary>Historical success rate.</summary>
    public float OutcomeHistory { get; set; }

    /// <summary>Information gain potential.</summary>
    public float Novelty { get; set; }

    /// <summary>Additional pipeline-specific dimensions.</summary>
    public float[] Extended { get; set; }

    public static DimensionalScores Default() => new()
    {
        Coherence = 0.5f,
        Stability = 0.5f,
        ContextFit = 0.5f,
        Risk = 0.3f,
        Reversibility = 0.7f,
        OutcomeHistory = 0.5f,
        Novelty = 0.3f,
        Extended = Array.Empty<float>()
    };

    /// <summary>Decay uncertified dimensions toward baseline.</summary>
    public void Decay(float rate = 0.95f)
    {
        Coherence = Coherence * rate + 0.5f * (1 - rate);
        Stability = Stability * rate + 0.5f * (1 - rate);
        ContextFit = ContextFit * rate + 0.5f * (1 - rate);
        OutcomeHistory = OutcomeHistory * rate + 0.5f * (1 - rate);
    }
}

/// <summary>
/// Weights for combining dimensional scores.
/// </summary>
public readonly struct DimensionalWeights
{
    public float Coherence { get; init; }
    public float Stability { get; init; }
    public float ContextFit { get; init; }
    public float Risk { get; init; }
    public float Reversibility { get; init; }
    public float OutcomeHistory { get; init; }
    public float Novelty { get; init; }

    public static DimensionalWeights Default() => new()
    {
        Coherence = 0.25f,
        Stability = 0.20f,
        ContextFit = 0.15f,
        Risk = 0.15f,
        Reversibility = 0.10f,
        OutcomeHistory = 0.10f,
        Novelty = 0.05f
    };

    public float Sum => Coherence + Stability + ContextFit + Risk +
                        Reversibility + OutcomeHistory + Novelty;

    public DimensionalWeights Normalized()
    {
        float s = Sum;
        if (s < 0.001f) return Default();
        return new DimensionalWeights
        {
            Coherence = Coherence / s,
            Stability = Stability / s,
            ContextFit = ContextFit / s,
            Risk = Risk / s,
            Reversibility = Reversibility / s,
            OutcomeHistory = OutcomeHistory / s,
            Novelty = Novelty / s
        };
    }
}

/// <summary>
/// Thresholds for gate decisions.
/// </summary>
public readonly struct GateThresholds
{
    public float AllowThreshold { get; init; }
    public float ProbeThreshold { get; init; }
    public float MaxRisk { get; init; }
    public float MinCoherence { get; init; }
    public float Hysteresis { get; init; }
    public DimensionalWeights Weights { get; init; }

    public static GateThresholds Default() => new()
    {
        AllowThreshold = 0.70f,
        ProbeThreshold = 0.40f,
        MaxRisk = 0.80f,
        MinCoherence = 0.30f,
        Hysteresis = 0.05f,
        Weights = DimensionalWeights.Default()
    };

    public static GateThresholds Strict() => new()
    {
        AllowThreshold = 0.80f,
        ProbeThreshold = 0.50f,
        MaxRisk = 0.60f,
        MinCoherence = 0.40f,
        Hysteresis = 0.08f,
        Weights = DimensionalWeights.Default()
    };

    public static GateThresholds Lenient() => new()
    {
        AllowThreshold = 0.55f,
        ProbeThreshold = 0.30f,
        MaxRisk = 0.90f,
        MinCoherence = 0.20f,
        Hysteresis = 0.03f,
        Weights = DimensionalWeights.Default()
    };
}

/// <summary>
/// Certification Authority - validates claims at a gate.
/// </summary>
public interface ICertificationAuthority
{
    string Name { get; }
    float Weight { get; }
    CertificationResult Certify(object claim, DimensionalScores currentScores);
}

/// <summary>
/// Result of certification attempt.
/// </summary>
public readonly struct CertificationResult
{
    public bool Passed { get; init; }
    public float Score { get; init; }
    public string Reason { get; init; }
    public DimensionalScores UpdatedScores { get; init; }

    public static CertificationResult Pass(float score, DimensionalScores scores, string reason = "") =>
        new() { Passed = true, Score = score, UpdatedScores = scores, Reason = reason };

    public static CertificationResult Fail(string reason, DimensionalScores scores) =>
        new() { Passed = false, Score = 0, UpdatedScores = scores, Reason = reason };
}

/// <summary>
/// Sink - captures and learns from rejected claims.
/// </summary>
public interface ISink
{
    string Name { get; }
    bool ShouldCapture(object claim, GateDecision decision);
    void Capture(object claim, GateDecision decision);
    int CapturedCount { get; }
}

/// <summary>
/// Base class for pipeline sinks.
/// </summary>
public abstract class BaseSink : ISink
{
    public abstract string Name { get; }
    private int _capturedCount;
    public int CapturedCount => _capturedCount;

    public abstract bool ShouldCapture(object claim, GateDecision decision);

    public virtual void Capture(object claim, GateDecision decision)
    {
        _capturedCount++;
    }
}

/// <summary>
/// Probe action - safe evidence gathering.
/// </summary>
public readonly struct ProbeAction
{
    public ProbeType Type { get; init; }
    public object Target { get; init; }
    public float SafetyMargin { get; init; }
    public TimeSpan Duration { get; init; }
    public string Reasoning { get; init; }
}

/// <summary>
/// Types of probes.
/// </summary>
public enum ProbeType
{
    Observe,        // Just watch
    SmallStep,      // Micro-movement for parallax
    Query,          // Request information
    Test,           // Safe test action
    Wait            // Wait and re-evaluate
}

/// <summary>
/// Certified claim with authority chain.
/// </summary>
public class CertifiedClaim<T>
{
    public T Value { get; init; } = default!;
    public float AuthorityScore { get; init; }
    public DimensionalScores Scores { get; init; }
    public List<string> CertificationChain { get; init; } = new();
    public DateTime CertifiedAt { get; init; }
    public int FramesCertified { get; set; }
    public bool IsExpired => FramesCertified > 300; // ~10 seconds

    public void Recertify(float score)
    {
        FramesCertified++;
    }

    public void Decay(float rate)
    {
        var scores = Scores;
        scores.Decay(rate);
    }
}
