using OKET.Core.Operators;
using OKET.Core.Cognition;

namespace OKET.Agent.Cognition;

/// <summary>
/// Registry of all cues in the system.
///
/// Cues self-organize by discovering simple predicates that
/// reliably predict survival through the sink.
///
/// The registry:
/// - Tracks all known cues
/// - Evaluates which cues fire each frame
/// - Computes aggregate strain discount
/// - Records results for credit assignment
/// - Promotes/demotes cues based on performance
/// </summary>
public sealed class CueRegistry
{
    private readonly Dictionary<string, Cue> _cues = new();
    private readonly List<string> _firedThisFrame = new();
    private readonly object _lock = new();

    // Snapshot of last evaluation
    private float _lastStrainDiscount;
    private int _lastFiredCount;
    private int _lastBoundaryCount;

    /// <summary>
    /// Total strain discount from all firing cues.
    /// </summary>
    public float TotalStrainDiscount => _lastStrainDiscount;

    /// <summary>
    /// Number of cues that fired this frame.
    /// </summary>
    public int FiredCueCount => _lastFiredCount;

    /// <summary>
    /// Number of cues that have hardened into boundaries.
    /// </summary>
    public int BoundaryCount => _lastBoundaryCount;

    /// <summary>
    /// All registered cues.
    /// </summary>
    public IReadOnlyCollection<Cue> AllCues
    {
        get
        {
            lock (_lock) return _cues.Values.ToList();
        }
    }

    /// <summary>
    /// All cues that have become boundaries.
    /// </summary>
    public IEnumerable<Cue> Boundaries
    {
        get
        {
            lock (_lock) return _cues.Values.Where(c => c.IsBoundary).ToList();
        }
    }

    /// <summary>
    /// Register a new cue.
    /// </summary>
    public void Register(Cue cue)
    {
        lock (_lock)
        {
            _cues[cue.Id] = cue;
        }
    }

    /// <summary>
    /// Evaluate all cues against current cognitive state.
    /// Returns aggregate strain discount.
    /// </summary>
    public float Evaluate(
        ZScoreStack zScores,
        InteroceptiveState feeling,
        BeliefState belief)
    {
        lock (_lock)
        {
            _firedThisFrame.Clear();
            float totalDiscount = 0f;
            int boundaryCount = 0;

            foreach (var cue in _cues.Values)
            {
                // Apply natural decay
                cue.ApplyDecay();

                // Check if cue fires
                bool fires = EvaluateCue(cue, zScores, feeling, belief);

                if (fires)
                {
                    _firedThisFrame.Add(cue.Id);
                    totalDiscount += cue.GetStrainDiscount();
                }

                if (cue.IsBoundary)
                    boundaryCount++;
            }

            _lastStrainDiscount = totalDiscount;
            _lastFiredCount = _firedThisFrame.Count;
            _lastBoundaryCount = boundaryCount;

            return totalDiscount;
        }
    }

    /// <summary>
    /// Record outcome after a commit decision.
    /// This is the credit assignment step.
    /// </summary>
    public void RecordOutcome(bool survived, float strainDelta, float outcomeDelta)
    {
        lock (_lock)
        {
            // Credit/blame cues that fired before this outcome
            foreach (var cueId in _firedThisFrame)
            {
                if (_cues.TryGetValue(cueId, out var cue))
                {
                    cue.RecordResult(true, survived, strainDelta, outcomeDelta);
                }
            }
        }
    }

    /// <summary>
    /// Get a summary of cue state for diagnostics.
    /// </summary>
    public string GetSummary()
    {
        lock (_lock)
        {
            var boundaries = _cues.Values.Where(c => c.IsBoundary).ToList();
            var reliable = _cues.Values.Where(c => c.Reliability > 0.7f && !c.IsBoundary).ToList();
            var learning = _cues.Values.Where(c => c.Reliability is > 0.4f and <= 0.7f).ToList();
            var weak = _cues.Values.Where(c => c.Reliability <= 0.4f).ToList();

            return $"""
                CueRegistry: {_cues.Count} total, {_lastFiredCount} fired, discount={_lastStrainDiscount:F3}
                  Boundaries: {boundaries.Count} [{string.Join(", ", boundaries.Select(c => c.Id))}]
                  Reliable: {reliable.Count}
                  Learning: {learning.Count}
                  Weak: {weak.Count}
                """;
        }
    }

    // Built-in cue evaluators
    private static bool EvaluateCue(
        Cue cue,
        ZScoreStack zScores,
        InteroceptiveState feeling,
        BeliefState belief)
    {
        return cue.Id switch
        {
            // Multimodal agreement cues
            "audio_visual_agree" => zScores.Z1_PerceptualAgreement > 0.5f,
            "audio_visual_strong" => zScores.Z1_PerceptualAgreement > 1.0f,
            "audio_visual_disagree" => zScores.Z1_PerceptualAgreement < -0.5f,

            // Stability cues
            "belief_stable" => zScores.Z2_BeliefStability > 0.5f,
            "belief_very_stable" => zScores.Z2_BeliefStability > 1.0f,
            "belief_unstable" => zScores.Z2_BeliefStability < -0.5f,

            // Control efficacy cues
            "control_working" => zScores.Z3_ControlEfficacy > 0.3f,
            "control_strong" => zScores.Z3_ControlEfficacy > 0.7f,
            "control_failing" => zScores.Z3_ControlEfficacy < -0.3f,

            // Strain cues
            "low_strain" => zScores.SystemStrain < 0.5f,
            "moderate_strain" => zScores.SystemStrain is >= 0.5f and < 1.5f,
            "high_strain" => zScores.SystemStrain >= 1.5f,
            "critical_strain" => zScores.SystemStrain >= 2.5f,

            // Feeling cues
            "high_trust" => feeling.PerceptionTrust > 1.0f,
            "low_trust" => feeling.PerceptionTrust < 0.7f,
            "high_commitment" => feeling.CommitmentConfidence > 1.2f,
            "hesitating" => feeling.ShouldHesitate,
            "urgent" => feeling.MustActNow,

            // Outcome cues
            "improving" => feeling.OutcomeTrend > 0.2f,
            "declining" => feeling.OutcomeTrend < -0.2f,
            "stable_outcomes" => Math.Abs(feeling.OutcomeTrend) < 0.1f,

            // Validity cues
            "valid_posture" => feeling.Validity > 0.6f,
            "weak_posture" => feeling.Validity < 0.4f,
            "compromised" => feeling.ValidityCompromised,

            // Threat cues
            "high_threat" => feeling.ThreatPressure > 0.6f,
            "low_threat" => feeling.ThreatPressure < 0.3f,
            "moderate_threat" => feeling.ThreatPressure is >= 0.3f and <= 0.6f,

            // Belief state cues
            "has_target" => belief.PrimaryTarget != null,
            "multiple_threats" => belief.DetectedEntities.Count(e => e.IsHostile) > 2,
            "single_threat" => belief.DetectedEntities.Count(e => e.IsHostile) == 1,
            "no_threats" => !belief.DetectedEntities.Any(e => e.IsHostile),

            // Combined cues (these are the powerful ones)
            "safe_to_commit" => zScores.SystemStrain < 1.0f && feeling.Validity > 0.5f && !feeling.ShouldHesitate,
            "danger_confirmed" => feeling.ThreatPressure > 0.5f && zScores.Z1_PerceptualAgreement > 0.3f,
            "should_retreat" => feeling.ThreatPressure > 0.7f && feeling.ControlConfidence < 0.4f,
            "can_engage" => belief.PrimaryTarget != null && feeling.ControlConfidence > 0.5f && feeling.Validity > 0.5f,

            _ => false
        };
    }

    /// <summary>
    /// Initialize with default cue set.
    /// </summary>
    public static CueRegistry CreateDefault()
    {
        var registry = new CueRegistry();

        // Multimodal agreement
        registry.Register(new Cue("audio_visual_agree", "Audio and visual modalities agree", 0.1f));
        registry.Register(new Cue("audio_visual_strong", "Strong audio-visual agreement", 0.1f));
        registry.Register(new Cue("audio_visual_disagree", "Audio and visual disagree", 0.1f));

        // Stability
        registry.Register(new Cue("belief_stable", "Beliefs are stable", 0.05f));
        registry.Register(new Cue("belief_very_stable", "Beliefs are very stable", 0.05f));
        registry.Register(new Cue("belief_unstable", "Beliefs are unstable", 0.05f));

        // Control
        registry.Register(new Cue("control_working", "Actions are having effect", 0.1f));
        registry.Register(new Cue("control_strong", "Strong control efficacy", 0.1f));
        registry.Register(new Cue("control_failing", "Actions not working", 0.1f));

        // Strain
        registry.Register(new Cue("low_strain", "System strain is low", 0.05f));
        registry.Register(new Cue("moderate_strain", "System strain is moderate", 0.05f));
        registry.Register(new Cue("high_strain", "System strain is high", 0.05f));
        registry.Register(new Cue("critical_strain", "System strain is critical", 0.05f));

        // Feeling
        registry.Register(new Cue("high_trust", "High perception trust", 0.05f));
        registry.Register(new Cue("low_trust", "Low perception trust", 0.05f));
        registry.Register(new Cue("high_commitment", "High commitment confidence", 0.05f));
        registry.Register(new Cue("hesitating", "Should hesitate", 0.05f));
        registry.Register(new Cue("urgent", "Must act now", 0.05f));

        // Outcomes
        registry.Register(new Cue("improving", "Outcomes improving", 0.05f));
        registry.Register(new Cue("declining", "Outcomes declining", 0.05f));
        registry.Register(new Cue("stable_outcomes", "Outcomes stable", 0.05f));

        // Validity
        registry.Register(new Cue("valid_posture", "Posture is valid", 0.05f));
        registry.Register(new Cue("weak_posture", "Posture is weak", 0.05f));
        registry.Register(new Cue("compromised", "Validity compromised", 0.05f));

        // Threat
        registry.Register(new Cue("high_threat", "High threat pressure", 0.1f));
        registry.Register(new Cue("low_threat", "Low threat pressure", 0.1f));
        registry.Register(new Cue("moderate_threat", "Moderate threat", 0.1f));

        // Belief state
        registry.Register(new Cue("has_target", "Has primary target", 0.1f));
        registry.Register(new Cue("multiple_threats", "Multiple threats detected", 0.15f));
        registry.Register(new Cue("single_threat", "Single threat detected", 0.1f));
        registry.Register(new Cue("no_threats", "No threats detected", 0.1f));

        // Combined (more expensive but more predictive)
        registry.Register(new Cue("safe_to_commit", "Safe to commit to action", 0.2f));
        registry.Register(new Cue("danger_confirmed", "Danger confirmed multimodally", 0.2f));
        registry.Register(new Cue("should_retreat", "Should retreat", 0.25f));
        registry.Register(new Cue("can_engage", "Can engage target", 0.2f));

        return registry;
    }
}
