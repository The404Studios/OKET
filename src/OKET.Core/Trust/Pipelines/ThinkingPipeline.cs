namespace OKET.Core.Trust.Pipelines;

/// <summary>
/// Thinking Pipeline - Proposal Generation + Simulation.
///
/// Thinking is hypothesis/plan/explanation generation.
///
/// RAW COGNITION:
///   Input: certified percepts + certified feeling + goals
///   Transform: generate hypotheses / plans / explanations
///   Output: ThoughtCandidates[]
///
/// TRUSTED THINKING:
///   Gates (CAs):
///     CA-T0 Logical Coherence: contradictions? missing premises?
///     CA-T1 Evidence Binding: does it bind to certified percepts?
///     CA-T2 Cost Check: does it require ignoring known constraints?
///     CA-T3 Predictive Check: does it predict what happens next?
///     CA-T4 Humility Gate: does it admit uncertainty where warranted?
///
///   Sinks:
///     - Rumination sink (repetitive, no new evidence)
///     - Fantasy sink (unbound to perception)
///     - Ego Defense sink (motivated reasoning)
///
///   Output: CertifiedThoughts[] (ranked, with uncertainty)
///
/// RULE: Thinking becomes trusted only when it can survive contact
///       with evidence + prediction.
/// </summary>
public sealed class ThinkingPipeline
{
    // Certification authorities
    private readonly LogicalCoherenceCA _caLogical = new();
    private readonly EvidenceBindingCA _caEvidence = new();
    private readonly CostCheckCA _caCost = new();
    private readonly PredictiveCheckCA _caPredictive = new();
    private readonly HumilityGateCA _caHumility = new();

    // Sinks
    private readonly RuminationSink _ruminationSink = new();
    private readonly FantasySink _fantasySink = new();
    private readonly EgoDefenseSink _egoDefenseSink = new();

    // State tracking
    private readonly Queue<ThoughtCandidate> _recentThoughts = new();
    private readonly Dictionary<string, int> _thoughtRepetitions = new();
    private readonly List<CertifiedThought> _certified = new();
    private GateState _gateState = GateState.Probe;

    // Predictions for validation
    private readonly Queue<PredictionRecord> _predictions = new();

    // Thresholds
    private readonly GateThresholds _thresholds;

    // Statistics
    private int _totalGenerated;
    private int _totalCertified;
    private int _sunk;
    private int _predictionsValidated;
    private int _predictionsCorrect;

    public IReadOnlyList<CertifiedThought> Certified => _certified;
    public GateState CurrentGateState => _gateState;
    public float CertificationRate => _totalGenerated > 0 ? (float)_totalCertified / _totalGenerated : 0;
    public float PredictiveAccuracy => _predictionsValidated > 0 ? (float)_predictionsCorrect / _predictionsValidated : 0.5f;

    public ThinkingPipeline()
    {
        _thresholds = new GateThresholds
        {
            AllowThreshold = 0.70f,
            ProbeThreshold = 0.40f,
            MaxRisk = 0.75f,
            MinCoherence = 0.35f,
            Hysteresis = 0.05f,
            Weights = new DimensionalWeights
            {
                Coherence = 0.25f,
                Stability = 0.15f,
                ContextFit = 0.20f,
                Risk = 0.10f,
                Reversibility = 0.05f,
                OutcomeHistory = 0.15f,
                Novelty = 0.10f
            }.Normalized()
        };
    }

    /// <summary>
    /// Process thinking input and generate (potentially) certified thoughts.
    /// </summary>
    public ThinkingResult Process(ThinkingInput input)
    {
        // === STAGE 1: GENERATE THOUGHT CANDIDATES ===
        var candidates = GenerateCandidates(input);
        _totalGenerated += candidates.Count;

        var results = new List<ThoughtResult>();
        _certified.Clear();

        foreach (var candidate in candidates)
        {
            var result = ProcessCandidate(candidate, input);
            results.Add(result);

            if (result.IsCertified && result.CertifiedThought != null)
            {
                _certified.Add(result.CertifiedThought);
                _totalCertified++;
            }
            else if (result.SunkTo != null)
            {
                _sunk++;
            }
        }

        // === UPDATE PREDICTIONS ===
        CheckPendingPredictions(input);

        // Sort certified by authority
        var ranked = _certified.OrderByDescending(t => t.AuthorityScore).ToList();

        return new ThinkingResult
        {
            Candidates = candidates,
            Results = results,
            CertifiedThoughts = ranked,
            TopThought = ranked.FirstOrDefault(),
            GateState = _gateState
        };
    }

    /// <summary>
    /// Generate thought candidates from input.
    /// </summary>
    private List<ThoughtCandidate> GenerateCandidates(ThinkingInput input)
    {
        var candidates = new List<ThoughtCandidate>();

        // Generate based on certified percepts
        foreach (var percept in input.CertifiedPercepts)
        {
            // Hypothesis about what this is
            candidates.Add(new ThoughtCandidate
            {
                Type = ThoughtType.Hypothesis,
                Content = $"Percept {percept.Id} is {percept.Label}",
                Confidence = percept.Confidence,
                BoundPercepts = new[] { percept.Id },
                Source = ThoughtSource.Perception
            });

            // Plan based on percept
            if (percept.IsThreat)
            {
                candidates.Add(new ThoughtCandidate
                {
                    Type = ThoughtType.Plan,
                    Content = $"Engage threat {percept.Id}",
                    Confidence = 0.6f * input.Feeling.Intensity,
                    BoundPercepts = new[] { percept.Id },
                    Source = ThoughtSource.Reaction,
                    ProposedAction = DecisionType.Engage
                });

                candidates.Add(new ThoughtCandidate
                {
                    Type = ThoughtType.Plan,
                    Content = $"Kite threat {percept.Id}",
                    Confidence = 0.5f,
                    BoundPercepts = new[] { percept.Id },
                    Source = ThoughtSource.Reaction,
                    ProposedAction = DecisionType.Kite
                });
            }

            if (percept.IsOpportunity)
            {
                candidates.Add(new ThoughtCandidate
                {
                    Type = ThoughtType.Plan,
                    Content = $"Approach opportunity {percept.Id}",
                    Confidence = 0.7f,
                    BoundPercepts = new[] { percept.Id },
                    Source = ThoughtSource.Perception,
                    ProposedAction = DecisionType.Approach
                });
            }
        }

        // Generate based on feeling
        if (input.Feeling.Type == FeelingType.Fear && input.Feeling.AuthorityScore > 0.5f)
        {
            candidates.Add(new ThoughtCandidate
            {
                Type = ThoughtType.Plan,
                Content = "Retreat to safety",
                Confidence = input.Feeling.Intensity * 0.8f,
                Source = ThoughtSource.Feeling,
                ProposedAction = DecisionType.Flee
            });
        }

        if (input.Feeling.Type == FeelingType.Calm && input.CertifiedPercepts.Count == 0)
        {
            candidates.Add(new ThoughtCandidate
            {
                Type = ThoughtType.Plan,
                Content = "Explore environment",
                Confidence = 0.5f,
                Source = ThoughtSource.Feeling,
                ProposedAction = DecisionType.Explore
            });
        }

        // Default observation thought
        if (candidates.Count == 0)
        {
            candidates.Add(new ThoughtCandidate
            {
                Type = ThoughtType.Plan,
                Content = "Observe and gather information",
                Confidence = 0.7f,
                Source = ThoughtSource.Default,
                ProposedAction = DecisionType.Observe
            });
        }

        return candidates;
    }

    /// <summary>
    /// Process a single thought candidate.
    /// </summary>
    private ThoughtResult ProcessCandidate(ThoughtCandidate candidate, ThinkingInput input)
    {
        // Track for repetition detection
        TrackThought(candidate);

        // === COMPUTE DIMENSIONAL SCORES ===
        var scores = ComputeScores(candidate, input);

        // === RUN CERTIFICATION AUTHORITIES ===
        var caResults = new List<CertificationResult>
        {
            _caLogical.Certify(candidate, scores),
            _caEvidence.Certify(candidate, input.CertifiedPercepts, scores),
            _caCost.Certify(candidate, input, scores),
            _caPredictive.Certify(candidate, _predictions, PredictiveAccuracy, scores),
            _caHumility.Certify(candidate, scores)
        };

        // Aggregate
        scores = AggregateCaResults(caResults, scores);

        // === GATE DECISION ===
        var decision = CognitiveGate.Evaluate(scores, _thresholds, _gateState);
        _gateState = decision.State;

        // === SINK OR PROMOTE ===
        if (decision.ShouldSink)
        {
            RouteToSinks(candidate, decision);
            return new ThoughtResult
            {
                Candidate = candidate,
                GateDecision = decision,
                IsCertified = false,
                SunkTo = GetActiveSink(candidate, decision)
            };
        }

        if (decision.ShouldProbe)
        {
            return new ThoughtResult
            {
                Candidate = candidate,
                GateDecision = decision,
                IsCertified = false,
                ProbeAction = GenerateProbe(candidate, scores)
            };
        }

        // === CERTIFY ===
        var certified = Certify(candidate, scores, caResults, input);

        // Register prediction if thought makes one
        if (candidate.Type == ThoughtType.Prediction ||
            (candidate.Type == ThoughtType.Plan && candidate.ExpectedOutcome != null))
        {
            RegisterPrediction(candidate, certified);
        }

        return new ThoughtResult
        {
            Candidate = candidate,
            GateDecision = decision,
            IsCertified = true,
            CertifiedThought = certified
        };
    }

    /// <summary>
    /// Compute dimensional scores for thought.
    /// </summary>
    private DimensionalScores ComputeScores(ThoughtCandidate candidate, ThinkingInput input)
    {
        return new DimensionalScores
        {
            Coherence = ComputeLogicalCoherence(candidate),
            Stability = ComputeStability(candidate),
            ContextFit = ComputeEvidenceBinding(candidate, input),
            Risk = ComputeThoughtRisk(candidate, input),
            Reversibility = candidate.Type == ThoughtType.Plan ? 0.6f : 0.9f,
            OutcomeHistory = GetThoughtHistory(candidate),
            Novelty = candidate.Source == ThoughtSource.Insight ? 0.8f : 0.3f
        };
    }

    private float ComputeLogicalCoherence(ThoughtCandidate thought)
    {
        // Check for obvious contradictions
        if (thought.Content.Contains("but") && thought.Content.Contains("and"))
            return 0.6f; // Hedged

        if (thought.Confidence > 0.9f && thought.BoundPercepts.Length == 0)
            return 0.4f; // High confidence without evidence

        return thought.Confidence;
    }

    private float ComputeStability(ThoughtCandidate thought)
    {
        // Check if similar thoughts have been generated recently
        string key = GetThoughtKey(thought);
        if (_thoughtRepetitions.TryGetValue(key, out int count))
        {
            if (count > 5)
                return 0.3f; // Ruminating
            return 0.7f + count * 0.05f; // Some repetition is stability
        }
        return 0.5f; // New thought
    }

    private float ComputeEvidenceBinding(ThoughtCandidate thought, ThinkingInput input)
    {
        if (thought.BoundPercepts.Length == 0)
            return thought.Source == ThoughtSource.Feeling ? 0.5f : 0.3f;

        // Check if bound percepts are actually in input
        int bound = thought.BoundPercepts.Count(id =>
            input.CertifiedPercepts.Any(p => p.Id == id));

        return bound / (float)thought.BoundPercepts.Length;
    }

    private float ComputeThoughtRisk(ThoughtCandidate thought, ThinkingInput input)
    {
        // Plans have execution risk
        if (thought.Type == ThoughtType.Plan)
        {
            if (thought.ProposedAction == DecisionType.Engage)
                return 0.4f + input.ThreatCount * 0.1f;
            if (thought.ProposedAction == DecisionType.Flee)
                return 0.2f;
        }

        return 0.1f; // Thoughts themselves are low risk
    }

    private float GetThoughtHistory(ThoughtCandidate thought)
    {
        // Would track outcome history of similar thoughts
        return 0.5f;
    }

    private static DimensionalScores AggregateCaResults(
        List<CertificationResult> results,
        DimensionalScores baseScores)
    {
        float passCount = results.Count(r => r.Passed);
        float avgScore = results.Where(r => r.Passed).Select(r => r.Score).DefaultIfEmpty(0).Average();

        var scores = baseScores;
        scores.Coherence = (scores.Coherence + avgScore) / 2f;
        scores.Stability *= passCount / results.Count;

        return scores;
    }

    private void TrackThought(ThoughtCandidate thought)
    {
        string key = GetThoughtKey(thought);
        _thoughtRepetitions[key] = _thoughtRepetitions.GetValueOrDefault(key, 0) + 1;

        _recentThoughts.Enqueue(thought);
        while (_recentThoughts.Count > 50)
            _recentThoughts.Dequeue();

        // Decay repetition counts
        foreach (var k in _thoughtRepetitions.Keys.ToList())
            _thoughtRepetitions[k] = Math.Max(0, _thoughtRepetitions[k] - 1);
    }

    private static string GetThoughtKey(ThoughtCandidate thought) =>
        $"{thought.Type}:{thought.ProposedAction}:{thought.Content.GetHashCode()}";

    private void RouteToSinks(ThoughtCandidate thought, GateDecision decision)
    {
        if (_ruminationSink.ShouldCapture(thought, decision))
            _ruminationSink.Capture(thought, decision);
        else if (_fantasySink.ShouldCapture(thought, decision))
            _fantasySink.Capture(thought, decision);
        else if (_egoDefenseSink.ShouldCapture(thought, decision))
            _egoDefenseSink.Capture(thought, decision);
    }

    private string? GetActiveSink(ThoughtCandidate thought, GateDecision decision)
    {
        if (_ruminationSink.ShouldCapture(thought, decision)) return _ruminationSink.Name;
        if (_fantasySink.ShouldCapture(thought, decision)) return _fantasySink.Name;
        if (_egoDefenseSink.ShouldCapture(thought, decision)) return _egoDefenseSink.Name;
        return null;
    }

    private static ProbeAction GenerateProbe(ThoughtCandidate thought, DimensionalScores scores)
    {
        return new ProbeAction
        {
            Type = thought.Type == ThoughtType.Plan ? ProbeType.SmallStep : ProbeType.Observe,
            Target = thought,
            SafetyMargin = 0.8f,
            Duration = TimeSpan.FromMilliseconds(300),
            Reasoning = $"Probe thought: evidence={scores.ContextFit:F2}"
        };
    }

    private static CertifiedThought Certify(
        ThoughtCandidate candidate,
        DimensionalScores scores,
        List<CertificationResult> caResults,
        ThinkingInput input)
    {
        return new CertifiedThought
        {
            Type = candidate.Type,
            Content = candidate.Content,
            ProposedAction = candidate.ProposedAction,
            AuthorityScore = scores.Coherence * scores.ContextFit * scores.Stability,
            Uncertainty = 1f - scores.Coherence,
            Scores = scores,
            BoundPercepts = candidate.BoundPercepts,
            CertificationChain = caResults.Where(r => r.Passed).Select(r => r.Reason).ToList(),
            CertifiedAt = DateTime.UtcNow
        };
    }

    private void RegisterPrediction(ThoughtCandidate thought, CertifiedThought certified)
    {
        _predictions.Enqueue(new PredictionRecord
        {
            ThoughtId = certified.GetHashCode(),
            Prediction = thought.ExpectedOutcome ?? thought.Content,
            Confidence = certified.AuthorityScore,
            CreatedAt = DateTime.UtcNow
        });

        while (_predictions.Count > 20)
            _predictions.Dequeue();
    }

    private void CheckPendingPredictions(ThinkingInput input)
    {
        // Check if any predictions can be validated
        foreach (var pred in _predictions.Where(p => !p.Validated))
        {
            // Simple validation: did the expected outcome occur?
            bool validated = input.CertifiedPercepts.Any(p => p.Label.Contains(pred.Prediction));
            if (validated || (DateTime.UtcNow - pred.CreatedAt).TotalSeconds > 5)
            {
                pred.Validated = true;
                pred.Correct = validated;
                _predictionsValidated++;
                if (validated) _predictionsCorrect++;
            }
        }
    }

    /// <summary>
    /// Decay uncertified state.
    /// </summary>
    public void Decay()
    {
        foreach (var thought in _certified)
        {
            thought.FramesSinceCertified++;
        }

        _certified.RemoveAll(t => t.FramesSinceCertified > 90); // ~3 seconds
    }

    public string GetDiagnostics()
    {
        return $"""
            === THINKING PIPELINE ===
            Generated: {_totalGenerated}
            Certified: {_totalCertified} ({CertificationRate:P0})
            Sunk: {_sunk}
            Current: {_certified.Count} thoughts
            Top: {_certified.FirstOrDefault()?.Content ?? "none"}
            Predictive Accuracy: {PredictiveAccuracy:P0}
            Sinks: rumination={_ruminationSink.CapturedCount}, fantasy={_fantasySink.CapturedCount}, ego={_egoDefenseSink.CapturedCount}
            =========================
            """;
    }
}

// ============== TYPES ==============

/// <summary>
/// Input for thinking pipeline.
/// </summary>
public readonly struct ThinkingInput
{
    public List<CertifiedPercept> CertifiedPercepts { get; init; }
    public CertifiedFeeling Feeling { get; init; }
    public List<Goal> Goals { get; init; }
    public int ThreatCount { get; init; }
    public float Health { get; init; }
}

/// <summary>
/// A certified percept (from perception pipeline).
/// </summary>
public readonly struct CertifiedPercept
{
    public int Id { get; init; }
    public string Label { get; init; }
    public float Confidence { get; init; }
    public bool IsThreat { get; init; }
    public bool IsOpportunity { get; init; }
}

/// <summary>
/// A goal.
/// </summary>
public readonly struct Goal
{
    public string Name { get; init; }
    public float Priority { get; init; }
}

/// <summary>
/// Thought candidate (uncertified).
/// </summary>
public sealed class ThoughtCandidate
{
    public ThoughtType Type { get; init; }
    public string Content { get; init; } = "";
    public float Confidence { get; init; }
    public int[] BoundPercepts { get; init; } = Array.Empty<int>();
    public ThoughtSource Source { get; init; }
    public DecisionType? ProposedAction { get; init; }
    public string? ExpectedOutcome { get; init; }
}

/// <summary>
/// Types of thoughts.
/// </summary>
public enum ThoughtType
{
    Hypothesis,   // What is X?
    Plan,         // What should I do?
    Explanation,  // Why did X happen?
    Prediction,   // What will happen next?
    Reflection    // What does this mean?
}

/// <summary>
/// Source of thought.
/// </summary>
public enum ThoughtSource
{
    Perception,   // From percepts
    Feeling,      // From affect
    Reaction,     // Reactive pattern
    Insight,      // Novel connection
    Default       // Fallback
}

/// <summary>
/// Certified thought.
/// </summary>
public sealed class CertifiedThought
{
    public ThoughtType Type { get; init; }
    public string Content { get; init; } = "";
    public DecisionType? ProposedAction { get; init; }
    public float AuthorityScore { get; init; }
    public float Uncertainty { get; init; }
    public DimensionalScores Scores { get; init; }
    public int[] BoundPercepts { get; init; } = Array.Empty<int>();
    public List<string> CertificationChain { get; init; } = new();
    public DateTime CertifiedAt { get; init; }
    public int FramesSinceCertified { get; set; }
}

/// <summary>
/// Result per thought candidate.
/// </summary>
public readonly struct ThoughtResult
{
    public ThoughtCandidate Candidate { get; init; }
    public GateDecision GateDecision { get; init; }
    public bool IsCertified { get; init; }
    public CertifiedThought? CertifiedThought { get; init; }
    public string? SunkTo { get; init; }
    public ProbeAction? ProbeAction { get; init; }
}

/// <summary>
/// Result of thinking pipeline.
/// </summary>
public readonly struct ThinkingResult
{
    public List<ThoughtCandidate> Candidates { get; init; }
    public List<ThoughtResult> Results { get; init; }
    public List<CertifiedThought> CertifiedThoughts { get; init; }
    public CertifiedThought? TopThought { get; init; }
    public GateState GateState { get; init; }
}

/// <summary>
/// Record of a prediction for validation.
/// </summary>
internal sealed class PredictionRecord
{
    public int ThoughtId { get; init; }
    public string Prediction { get; init; } = "";
    public float Confidence { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool Validated { get; set; }
    public bool Correct { get; set; }
}

// ============== CERTIFICATION AUTHORITIES ==============

/// <summary>
/// CA-T0: Logical Coherence - contradictions? missing premises?
/// </summary>
internal sealed class LogicalCoherenceCA
{
    public CertificationResult Certify(ThoughtCandidate thought, DimensionalScores scores)
    {
        // High confidence without evidence
        if (thought.Confidence > 0.9f && thought.BoundPercepts.Length == 0)
            return CertificationResult.Fail("High confidence without evidence", scores);

        // Contradictory action
        if (thought.Content.Contains("but also") || thought.Content.Contains("yet"))
            return CertificationResult.Fail("Contradictory thought", scores);

        return CertificationResult.Pass(scores.Coherence, scores, "Logically coherent");
    }
}

/// <summary>
/// CA-T1: Evidence Binding - binds to certified percepts?
/// </summary>
internal sealed class EvidenceBindingCA
{
    public CertificationResult Certify(
        ThoughtCandidate thought,
        List<CertifiedPercept> percepts,
        DimensionalScores scores)
    {
        if (thought.BoundPercepts.Length == 0)
        {
            // Allow feeling-based and default thoughts
            if (thought.Source is ThoughtSource.Feeling or ThoughtSource.Default)
                return CertificationResult.Pass(0.5f, scores, "Non-perceptual source");

            return CertificationResult.Fail("Unbound to perception", scores);
        }

        int bound = thought.BoundPercepts.Count(id => percepts.Any(p => p.Id == id));
        float binding = bound / (float)thought.BoundPercepts.Length;

        if (binding < 0.5f)
            return CertificationResult.Fail($"Weak evidence binding ({binding:F2})", scores);

        var updated = scores;
        updated.ContextFit = binding;
        return CertificationResult.Pass(binding, updated, $"Evidence bound ({binding:F2})");
    }
}

/// <summary>
/// CA-T2: Cost Check - ignoring known constraints?
/// </summary>
internal sealed class CostCheckCA
{
    public CertificationResult Certify(
        ThoughtCandidate thought,
        ThinkingInput input,
        DimensionalScores scores)
    {
        // Check if plan ignores resource constraints
        if (thought.Type == ThoughtType.Plan)
        {
            if (thought.ProposedAction == DecisionType.Engage && input.Health < 0.2f)
                return CertificationResult.Fail("Engage plan ignores low health", scores);

            if (thought.ProposedAction == DecisionType.Explore && input.ThreatCount > 2)
                return CertificationResult.Fail("Explore plan ignores active threats", scores);
        }

        return CertificationResult.Pass(0.8f, scores, "Cost check passed");
    }
}

/// <summary>
/// CA-T3: Predictive Check - predicts what happens next?
/// </summary>
internal sealed class PredictiveCheckCA
{
    public CertificationResult Certify(
        ThoughtCandidate thought,
        Queue<PredictionRecord> predictions,
        float accuracy,
        DimensionalScores scores)
    {
        // If we have prediction history, use it
        if (predictions.Count > 5 && accuracy < 0.3f)
        {
            // Poor prediction track record
            var updated = scores;
            updated.OutcomeHistory = accuracy;
            return CertificationResult.Pass(accuracy, updated, $"Weak predictive track record ({accuracy:F2})");
        }

        return CertificationResult.Pass(0.7f, scores, "Predictive check passed");
    }
}

/// <summary>
/// CA-T4: Humility Gate - admits uncertainty where warranted?
/// </summary>
internal sealed class HumilityGateCA
{
    public CertificationResult Certify(ThoughtCandidate thought, DimensionalScores scores)
    {
        // Very high confidence on novel/complex thoughts is suspect
        if (thought.Confidence > 0.9f && thought.Source == ThoughtSource.Insight)
            return CertificationResult.Fail("Overconfident on novel insight", scores);

        // Plans with no bound percepts shouldn't be very confident
        if (thought.Type == ThoughtType.Plan &&
            thought.BoundPercepts.Length == 0 &&
            thought.Confidence > 0.8f)
            return CertificationResult.Fail("Overconfident on unbound plan", scores);

        return CertificationResult.Pass(0.8f, scores, "Appropriate humility");
    }
}

// ============== SINKS ==============

/// <summary>
/// Rumination Sink - repetitive, no new evidence.
/// </summary>
internal sealed class RuminationSink : BaseSink
{
    public override string Name => "Rumination";

    public override bool ShouldCapture(object claim, GateDecision decision)
    {
        if (claim is not ThoughtCandidate thought) return false;

        // Same thought repeated with low stability score
        return decision.Scores.Stability < 0.4f &&
               decision.Scores.Novelty < 0.2f;
    }
}

/// <summary>
/// Fantasy Sink - unbound to perception.
/// </summary>
internal sealed class FantasySink : BaseSink
{
    public override string Name => "Fantasy";

    public override bool ShouldCapture(object claim, GateDecision decision)
    {
        if (claim is not ThoughtCandidate thought) return false;

        // Unbound + high confidence = fantasy
        return decision.Scores.ContextFit < 0.3f &&
               thought.Confidence > 0.7f &&
               thought.Source != ThoughtSource.Feeling;
    }
}

/// <summary>
/// Ego Defense Sink - motivated reasoning.
/// </summary>
internal sealed class EgoDefenseSink : BaseSink
{
    public override string Name => "EgoDefense";

    public override bool ShouldCapture(object claim, GateDecision decision)
    {
        if (claim is not ThoughtCandidate thought) return false;

        // Self-serving conclusion with poor evidence
        // Would need more context to detect properly
        return decision.Scores.ContextFit < 0.4f &&
               decision.Scores.OutcomeHistory < 0.4f &&
               thought.Type == ThoughtType.Explanation;
    }
}
