namespace OKET.Core.Cognition;

/// <summary>
/// Interoceptive state - the "feeling" layer.
/// This is NOT emotions as labels, but system-level regulators that:
/// - Measure prediction error
/// - Track control confidence
/// - Assess sensory agreement
/// - Compute global coherence
///
/// Feeling is confidence-weighted coherence over time.
/// It's how much trust to place in perception and belief.
/// </summary>
public sealed class InteroceptiveState
{
    /// <summary>Prediction error: did the world behave as expected? [0, 1]</summary>
    /// <remarks>Higher = more surprise, world didn't match predictions</remarks>
    public float PredictionError { get; init; }

    /// <summary>Threat pressure: how fast things are worsening [0, 1]</summary>
    /// <remarks>Higher = threat increasing, health dropping, situation degrading</remarks>
    public float ThreatPressure { get; init; }

    /// <summary>Control confidence: are my actions working? [0, 1]</summary>
    /// <remarks>Higher = actions produce expected outcomes</remarks>
    public float ControlConfidence { get; init; }

    /// <summary>Sensory alignment: do vision & audio agree? [0, 1]</summary>
    /// <remarks>Higher = modalities corroborate each other</remarks>
    public float SensoryAlignment { get; init; }

    /// <summary>Outcome trend: is survival improving or degrading? [-1, 1]</summary>
    /// <remarks>Positive = improving, negative = degrading</remarks>
    public float OutcomeTrend { get; init; }

    /// <summary>Belief stability: how much are beliefs oscillating? [0, 1]</summary>
    /// <remarks>Higher = more stable beliefs</remarks>
    public float BeliefStability { get; init; }

    /// <summary>Action coherence: are actions following a consistent pattern? [0, 1]</summary>
    /// <remarks>Higher = more coherent action sequences</remarks>
    public float ActionCoherence { get; init; }

    // --- Derived feeling states ---

    /// <summary>
    /// Global stability: overall system coherence [0, 1].
    /// Low stability = should slow down, gather more information.
    /// High stability = can act confidently.
    /// </summary>
    public float GlobalStability =>
        (SensoryAlignment * 0.25f +
         ControlConfidence * 0.25f +
         BeliefStability * 0.2f +
         (1f - PredictionError) * 0.2f +
         ActionCoherence * 0.1f);

    /// <summary>
    /// Urgency: how much pressure to act now [0, 1].
    /// High urgency + low stability = dangerous state.
    /// </summary>
    public float Urgency =>
        ThreatPressure * 0.5f +
        PredictionError * 0.2f +
        Math.Max(0, -OutcomeTrend) * 0.3f;

    /// <summary>
    /// Confidence to commit: should we act decisively or hesitate? [0, 1].
    /// </summary>
    public float CommitmentConfidence =>
        GlobalStability * 0.6f +
        ControlConfidence * 0.4f;

    /// <summary>
    /// Learning signal: how much should we update from this experience? [0, 1].
    /// High surprise + high control = important learning opportunity.
    /// </summary>
    public float LearningSalience =>
        PredictionError * 0.5f +
        ControlConfidence * 0.3f +
        Math.Abs(OutcomeTrend) * 0.2f;

    // --- Functional states (what the agent "feels") ---

    /// <summary>
    /// Functional analog of "anxiety" - high threat + low control.
    /// Increases scanning, hesitation, defensive posture.
    /// </summary>
    public float Anxiety =>
        Math.Clamp(ThreatPressure * (1f - ControlConfidence) * 1.5f, 0f, 1f);

    /// <summary>
    /// Functional analog of "frustration" - effort without results.
    /// Should trigger strategy change.
    /// </summary>
    public float Frustration =>
        Math.Clamp((1f - ControlConfidence) * PredictionError * 1.5f, 0f, 1f);

    /// <summary>
    /// Functional analog of "focus" - high control + stable beliefs.
    /// Enables exploitation, precise actions.
    /// </summary>
    public float Focus =>
        Math.Clamp(ControlConfidence * BeliefStability * 1.2f, 0f, 1f);

    /// <summary>
    /// Functional analog of "vigilance" - moderate threat + uncertainty.
    /// Increases attention, scan rate.
    /// </summary>
    public float Vigilance =>
        Math.Clamp(ThreatPressure * (1f - SensoryAlignment) * 1.3f, 0f, 1f);

    // --- Modulation outputs ---

    /// <summary>
    /// How much to trust current perception [0, 1].
    /// Low when sensory disagreement or high prediction error.
    /// </summary>
    public float PerceptionTrust =>
        SensoryAlignment * 0.5f + (1f - PredictionError) * 0.5f;

    /// <summary>
    /// How much to trust current belief [0, 1].
    /// Low when beliefs are unstable.
    /// </summary>
    public float BeliefTrust =>
        BeliefStability * 0.6f + ControlConfidence * 0.4f;

    /// <summary>
    /// Suggested action speed multiplier [0.5, 2.0].
    /// Slow down when uncertain, speed up when confident and urgent.
    /// </summary>
    public float ActionSpeedModifier =>
        Math.Clamp(0.5f + CommitmentConfidence * 0.5f + Urgency * 0.5f, 0.5f, 2.0f);

    /// <summary>
    /// Suggested learning rate multiplier [0.1, 2.0].
    /// Higher when experience is salient.
    /// </summary>
    public float LearningRateModifier =>
        Math.Clamp(0.5f + LearningSalience * 1.5f, 0.1f, 2.0f);

    /// <summary>
    /// Whether to delay commitment (wait for more information).
    /// </summary>
    public bool ShouldHesitate =>
        GlobalStability < 0.4f && Urgency < 0.7f;

    /// <summary>
    /// Whether to force action despite uncertainty.
    /// </summary>
    public bool MustActNow =>
        Urgency > 0.8f || ThreatPressure > 0.9f;

    /// <summary>
    /// Get a human-readable summary.
    /// </summary>
    public string GetSummary()
    {
        string dominantFeeling = (Anxiety, Frustration, Focus, Vigilance) switch
        {
            var (a, f, fo, v) when a > 0.6f => "ANXIOUS",
            var (a, f, fo, v) when f > 0.6f => "FRUSTRATED",
            var (a, f, fo, v) when fo > 0.6f => "FOCUSED",
            var (a, f, fo, v) when v > 0.6f => "VIGILANT",
            _ => "NEUTRAL"
        };

        return $"""
            Interoceptive State: {dominantFeeling}
              Prediction Error: {PredictionError:F2}
              Threat Pressure:  {ThreatPressure:F2}
              Control Conf:     {ControlConfidence:F2}
              Sensory Align:    {SensoryAlignment:F2}
              Outcome Trend:    {OutcomeTrend:F2}
              Global Stability: {GlobalStability:F2}
              Commitment Conf:  {CommitmentConfidence:F2}
              {(ShouldHesitate ? "[HESITATE]" : MustActNow ? "[ACT NOW]" : "[NORMAL]")}
            """;
    }
}
