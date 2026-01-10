namespace OKET.Core.Cognition;

/// <summary>
/// Interoceptive (feeling) state - the meta-sensor that stabilizes perception and decision.
///
/// This is NOT emotion as sentiment. This is posture.
/// Each output is an explicit control knob that modulates system thresholds.
///
/// Inputs (what feeds into feeling):
///   - Z₁ (perceptual agreement)
///   - Z₂ (belief volatility)
///   - Z₃ (control efficacy)
///   - Z₄ (global strain) ← THIS IS INPUT, not output
///   - Outcome trend
///   - Prediction error
///
/// Outputs (control knobs that change behavior):
///   - PerceptionTrust: raises/lowers minimum confidence to accept a detection
///   - CommitmentConfidence: raises/lowers hysteresis margin required to switch modes
///   - ActionSpeedModifier: scales mouse speed, key press durations, scan sweep rate
///   - LearningRateModifier: scales RL step size / replay weighting / memory write rate
///   - ShouldHesitate: gate which modes are allowed
///   - MustActNow: urgency override
/// </summary>
public sealed record InteroceptiveState
{
    // === RAW MEASUREMENTS (Z-score derived) ===

    /// <summary>Prediction error: did the world behave as expected? [0, 1]</summary>
    public float PredictionError { get; init; }

    /// <summary>Threat pressure: how fast things are worsening [0, 1]</summary>
    public float ThreatPressure { get; init; }

    /// <summary>Control confidence: are my actions working? [0, 1]</summary>
    public float ControlConfidence { get; init; }

    /// <summary>Sensory alignment: do vision & audio agree? [0, 1]</summary>
    public float SensoryAlignment { get; init; }

    /// <summary>Outcome trend: is survival improving or degrading? [-1, 1]</summary>
    public float OutcomeTrend { get; init; }

    /// <summary>Belief stability: how much are beliefs oscillating? [0, 1]</summary>
    public float BeliefStability { get; init; }

    /// <summary>Action coherence: are actions following a consistent pattern? [0, 1]</summary>
    public float ActionCoherence { get; init; }

    /// <summary>
    /// System strain from Z₄ [0, 3+].
    /// This is an INPUT to feeling, computed by the Z-score stack.
    /// </summary>
    public float SystemStrain { get; init; }

    // === CONTROL KNOBS (these modulate thresholds) ===

    /// <summary>
    /// Raises/lowers minimum confidence to accept a detection [0.5, 1.5].
    /// Low trust = require higher confidence from detections.
    /// Formula: base + sensory agreement boost - prediction error penalty - strain penalty.
    /// </summary>
    public float PerceptionTrust =>
        Math.Clamp(
            0.7f +
            SensoryAlignment * 0.3f -
            PredictionError * 0.2f -
            SystemStrain * 0.1f,
            0.5f, 1.5f);

    /// <summary>
    /// Raises/lowers hysteresis margin required to switch modes [0.5, 2.0].
    /// Low = easy to switch (twitchy). High = hard to switch (deliberate).
    /// Formula: belief stability scaled by strain.
    /// </summary>
    public float CommitmentConfidence =>
        Math.Clamp(
            0.6f +
            BeliefStability * 0.8f -
            SystemStrain * 0.15f,
            0.5f, 2.0f);

    /// <summary>
    /// Scales mouse speed, key press durations, scan sweep rate [0.5, 1.5].
    /// High urgency + high control = faster. Low control or high strain = slower.
    /// </summary>
    public float ActionSpeedModifier =>
        Math.Clamp(
            0.8f +
            Urgency * 0.3f +
            ControlConfidence * 0.2f -
            SystemStrain * 0.15f,
            0.5f, 1.5f);

    /// <summary>
    /// Scales RL step size / replay weighting / memory write rate [0.2, 2.0].
    /// High surprise = learn more. High strain = be cautious about learning.
    /// </summary>
    public float LearningRateModifier =>
        Math.Clamp(
            0.5f +
            PredictionError * 0.8f +
            Math.Abs(OutcomeTrend) * 0.4f -
            SystemStrain * 0.2f,
            0.2f, 2.0f);

    /// <summary>
    /// Gate: should we hesitate and gather more information?
    /// True when: low perception trust, unstable beliefs, or high system strain.
    /// </summary>
    public bool ShouldHesitate =>
        PerceptionTrust < 0.7f ||
        BeliefStability < 0.4f ||
        SystemStrain > 2.0f;

    /// <summary>
    /// Gate: must we act immediately regardless of uncertainty?
    /// True when: high threat pressure or rapidly declining situation.
    /// </summary>
    public bool MustActNow =>
        ThreatPressure > 0.7f ||
        (OutcomeTrend < -0.5f && ControlConfidence > 0.3f);

    /// <summary>
    /// Urgency level [0, 1]. High urgency can override hesitation.
    /// </summary>
    public float Urgency =>
        Math.Clamp(
            ThreatPressure * 0.4f +
            Math.Max(0, -OutcomeTrend) * 0.3f +
            PredictionError * 0.2f +
            SystemStrain * 0.1f,
            0f, 1f);

    // === GLOBAL STABILITY ===

    /// <summary>
    /// Overall stability [0, 1]. This is a SUMMARY, not an input.
    /// Low = system struggling to maintain coherence.
    /// </summary>
    public float GlobalStability =>
        Math.Clamp(
            SensoryAlignment * 0.25f +
            BeliefStability * 0.25f +
            ControlConfidence * 0.25f +
            (1f - Math.Min(SystemStrain, 3f) / 3f) * 0.25f,
            0f, 1f);

    // === DERIVED EMOTIONAL LABELS (for logging/debugging only) ===
    // These are human-readable summaries, NOT used for control.

    /// <summary>High when: high threat + low control + low trust.</summary>
    public float Anxiety =>
        Math.Clamp(
            ThreatPressure * 0.4f +
            (1f - ControlConfidence) * 0.3f +
            (1f - PerceptionTrust) * 0.3f,
            0f, 1f);

    /// <summary>High when: low control efficacy + negative outcomes.</summary>
    public float Frustration =>
        Math.Clamp(
            (1f - ControlConfidence) * 0.5f +
            Math.Max(0, -OutcomeTrend) * 0.5f,
            0f, 1f);

    /// <summary>High when: high control + stable beliefs + good outcomes.</summary>
    public float Focus =>
        Math.Clamp(
            ControlConfidence * 0.4f +
            BeliefStability * 0.3f +
            Math.Max(0, OutcomeTrend) * 0.3f,
            0f, 1f);

    /// <summary>High when: moderate threat + high alignment + stable.</summary>
    public float Vigilance =>
        Math.Clamp(
            ThreatPressure * 0.3f +
            SensoryAlignment * 0.35f +
            BeliefStability * 0.35f,
            0f, 1f);

    /// <summary>
    /// Get a human-readable summary.
    /// </summary>
    public string GetSummary()
    {
        var dominant = (Anxiety, Frustration, Focus, Vigilance) switch
        {
            var (a, _, _, _) when a > 0.6f => $"ANXIOUS({a:F2})",
            var (_, f, _, _) when f > 0.6f => $"FRUSTRATED({f:F2})",
            var (_, _, fo, _) when fo > 0.6f => $"FOCUSED({fo:F2})",
            var (_, _, _, v) when v > 0.6f => $"VIGILANT({v:F2})",
            _ => "NEUTRAL"
        };

        return $"""
            Feeling: {dominant}
              GlobalStability={GlobalStability:F2}, SystemStrain={SystemStrain:F2}
              Control Knobs:
                PerceptionTrust={PerceptionTrust:F2}
                CommitmentConf={CommitmentConfidence:F2}
                ActionSpeed={ActionSpeedModifier:F2}
                LearningRate={LearningRateModifier:F2}
              Gates: Hesitate={ShouldHesitate}, ActNow={MustActNow}
            """;
    }
}
