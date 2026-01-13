namespace OKET.Core.Trust.Pipelines;

/// <summary>
/// Cognitive Trust Orchestrator - Unified Pipeline Coordinator.
///
/// ARCHITECTURE:
///
///   [Raw Perception] → Perception Pipeline → [Certified Percepts]
///          ↓                                        ↓
///   [Body State] → Feeling Pipeline → [Certified Feeling]
///          ↓                                   ↓
///   [Context + Goals] → Thinking Pipeline → [Certified Thoughts]
///          ↓                                        ↓
///   [Options] → Decision Pipeline → [Certified Decision]
///          ↓                                   ↓
///   [Execution + Outcome] → Knowledge Pipeline → [Certified Knowledge]
///          ↓
///   MEMORY (feeds back to all pipelines)
///
/// CORE PRINCIPLES:
/// 1. Every pipeline uses the same ALLOW/PROBE/DENY gate pattern
/// 2. Certified outputs from one pipeline feed as trusted inputs to others
/// 3. Uncertified outputs can only be probed, not acted upon
/// 4. Everything decays without re-certification
/// 5. Sinks capture and learn from rejected claims
///
/// TRUST FLOW:
///   Raw → Formed → Claimed → Certified → State → Authorized → Memory
///   At each stage: ALLOW (promote) / PROBE (gather) / DENY (sink)
/// </summary>
public sealed class CognitiveTrustOrchestrator
{
    // The pipelines
    private readonly FeelingPipeline _feeling = new();
    private readonly ThinkingPipeline _thinking = new();
    private readonly DecisionPipeline _decision = new();
    private readonly KnowledgePipeline _knowledge = new();

    // Cross-pipeline state
    private CertifiedFeeling? _currentFeeling;
    private CertifiedThought? _currentThought;
    private DecisionProposal? _currentDecision;
    private readonly List<CertifiedPercept> _currentPercepts = new();

    // Global trust state
    private float _systemTrust = 0.5f;
    private float _systemStability = 0.5f;
    private int _consecutiveCertifiedCycles;
    private int _consecutiveFailedCycles;

    // Outcome tracking
    private readonly Queue<CycleOutcome> _outcomes = new();
    private const int MaxOutcomes = 100;

    // Frame counter
    private long _frameId;

    // Statistics
    private int _totalCycles;
    private int _fullyCertifiedCycles;
    private int _partialCycles;
    private int _failedCycles;

    public CertifiedFeeling? CurrentFeeling => _currentFeeling;
    public CertifiedThought? CurrentThought => _currentThought;
    public DecisionProposal? CurrentDecision => _currentDecision;
    public float SystemTrust => _systemTrust;
    public float SystemStability => _systemStability;
    public int TotalCycles => _totalCycles;
    public float FullCertificationRate => _totalCycles > 0 ? (float)_fullyCertifiedCycles / _totalCycles : 0;

    /// <summary>
    /// Process a complete cognitive cycle.
    /// </summary>
    public OrchestratorResult ProcessCycle(OrchestratorInput input)
    {
        _totalCycles++;
        _frameId = input.FrameId;

        // === STAGE 0: DECAY ALL PIPELINES ===
        DecayAll();

        // === STAGE 1: PERCEPTION → CERTIFIED PERCEPTS ===
        _currentPercepts.Clear();
        foreach (var perception in input.Perceptions)
        {
            // Convert perception to certified percept if it passes trust check
            if (perception.Confidence > 0.4f && perception.TrustScore > 0.5f)
            {
                _currentPercepts.Add(new CertifiedPercept
                {
                    Id = perception.Id,
                    Label = perception.Label,
                    Confidence = perception.Confidence * perception.TrustScore,
                    IsThreat = perception.IsThreat,
                    IsOpportunity = perception.IsOpportunity
                });
            }
        }

        // === STAGE 2: FEELING PIPELINE ===
        var feelingInput = BuildFeelingInput(input);
        var feelingResult = _feeling.Process(feelingInput);

        if (feelingResult.IsCertified)
        {
            _currentFeeling = feelingResult.CertifiedFeeling;
        }

        // === STAGE 3: THINKING PIPELINE ===
        var thinkingInput = BuildThinkingInput(input);
        var thinkingResult = _thinking.Process(thinkingInput);

        if (thinkingResult.TopThought != null)
        {
            _currentThought = thinkingResult.TopThought;
        }

        // === STAGE 4: DECISION PIPELINE ===
        var decisionContext = BuildDecisionContext(input);
        var decisionResult = _decision.Decide(decisionContext);
        _currentDecision = decisionResult;

        // === STAGE 5: KNOWLEDGE UPDATE ===
        // Submit observations as knowledge claims
        SubmitKnowledgeClaims(input, decisionResult);

        // Consolidate knowledge
        _knowledge.Consolidate();

        // === STAGE 6: COMPUTE CYCLE QUALITY ===
        var cycleQuality = ComputeCycleQuality(feelingResult, thinkingResult, decisionResult);

        UpdateTrustState(cycleQuality);

        // === STAGE 7: BUILD OUTPUT ===
        return new OrchestratorResult
        {
            FrameId = _frameId,
            CertifiedPercepts = _currentPercepts.ToList(),
            FeelingResult = feelingResult,
            ThinkingResult = thinkingResult,
            DecisionResult = decisionResult,
            RecommendedAction = GetRecommendedAction(decisionResult, thinkingResult),
            CycleQuality = cycleQuality,
            SystemTrust = _systemTrust,
            SystemStability = _systemStability,
            IsFullyCertified = cycleQuality.IsFullyCertified,
            ProbeActions = CollectProbeActions(feelingResult, thinkingResult)
        };
    }

    /// <summary>
    /// Record outcome of an action for learning.
    /// </summary>
    public void RecordOutcome(ActionOutcomeInput outcome)
    {
        // Record to feeling pipeline
        if (_currentFeeling != null)
        {
            _feeling.RecordOutcome(_currentFeeling.Type, outcome.Success);
        }

        // Record to thinking pipeline
        if (_currentThought?.PredictionId != null)
        {
            _thinking.ValidatePrediction(_currentThought.PredictionId.Value, outcome.Success);
        }

        // Record to decision pipeline
        _decision.UpdateWeights(outcome.Reward, outcome.Risk);

        // Record to knowledge pipeline
        if (outcome.KnowledgeId.HasValue)
        {
            _knowledge.RecordRetrievalOutcome(outcome.KnowledgeId.Value, outcome.Success, outcome.Impact);
        }

        // Track overall outcome
        _outcomes.Enqueue(new CycleOutcome
        {
            FrameId = _frameId,
            Success = outcome.Success,
            Reward = outcome.Reward,
            Risk = outcome.Risk
        });

        while (_outcomes.Count > MaxOutcomes)
            _outcomes.Dequeue();

        // Update system stability based on outcomes
        UpdateStabilityFromOutcomes();
    }

    /// <summary>
    /// Retrieve relevant knowledge for current situation.
    /// </summary>
    public RetrievalResult RetrieveKnowledge(List<string> tags, KnowledgeContext? context = null)
    {
        return _knowledge.Retrieve(new RetrievalQuery
        {
            Tags = tags,
            Context = context,
            MaxResults = 5
        });
    }

    private AffectInput BuildFeelingInput(OrchestratorInput input)
    {
        return new AffectInput
        {
            ThreatLevel = input.ThreatLevel,
            OpportunityLevel = input.OpportunityLevel,
            SystemStrain = input.SystemStrain,
            HealthChange = input.HealthChange,
            ResourceStatus = input.Health,
            NoveltyLevel = _currentPercepts.Count(p => p.Confidence < 0.6f) / Math.Max(1f, _currentPercepts.Count),
            ConflictingSignals = input.ConflictingSignals,
            Urgency = input.Urgency,
            RecentSuccess = input.RecentSuccess,
            RecentFailure = input.RecentFailure,
            PrimarySource = input.HasBodySignals ? AffectSource.Interoception : AffectSource.Perception
        };
    }

    private ThinkingInput BuildThinkingInput(OrchestratorInput input)
    {
        return new ThinkingInput
        {
            CertifiedPercepts = _currentPercepts,
            Feeling = _currentFeeling ?? new CertifiedFeeling { Type = FeelingType.Uncertain },
            Goals = input.Goals,
            ThreatCount = input.ThreatCount,
            Health = input.Health
        };
    }

    private DecisionContext BuildDecisionContext(OrchestratorInput input)
    {
        return new DecisionContext
        {
            Health = input.Health,
            Ammo = input.Ammo,
            ThreatCount = input.ThreatCount,
            OpportunityCount = input.OpportunityCount,
            UnderAttack = input.UnderAttack,
            PerceptionVolatility = input.ConflictingSignals,
            SystemStrain = input.SystemStrain,
            VisibleTargetIds = _currentPercepts.Select(p => p.Id).ToList(),
            PrimaryThreatId = _currentPercepts.FirstOrDefault(p => p.IsThreat).Id,
            PrimaryOpportunityId = _currentPercepts.FirstOrDefault(p => p.IsOpportunity).Id
        };
    }

    private void SubmitKnowledgeClaims(OrchestratorInput input, DecisionProposal decision)
    {
        // Submit situation-action associations
        if (_currentDecision != null && input.RecentSuccess)
        {
            var claim = new KnowledgeClaim
            {
                Type = KnowledgeType.Associative,
                Content = $"In {input.SituationTag}, action {_currentDecision.Type} succeeded",
                Tags = new List<string> { input.SituationTag, _currentDecision.Type.ToString() },
                Context = new KnowledgeContext
                {
                    ThreatLevel = input.ThreatLevel,
                    Health = input.Health,
                    ThreatCount = input.ThreatCount
                },
                Confidence = decision.Confidence,
                ObservationCount = 1,
                SuccessRate = 1f,
                ContextSpecificity = 0.5f,
                Source = KnowledgeSource.Experience
            };

            _knowledge.Process(claim);
        }
    }

    private CycleQuality ComputeCycleQuality(
        FeelingResult feeling,
        ThinkingResult thinking,
        DecisionProposal decision)
    {
        bool feelingCertified = feeling.IsCertified;
        bool thinkingCertified = thinking.CertifiedThoughts.Count > 0;
        bool decisionConfident = decision.Confidence > 0.5f;

        bool fullyCertified = feelingCertified && thinkingCertified && decisionConfident;
        bool partialCertified = feelingCertified || thinkingCertified || decisionConfident;

        if (fullyCertified)
            _fullyCertifiedCycles++;
        else if (partialCertified)
            _partialCycles++;
        else
            _failedCycles++;

        float certificationScore =
            (feelingCertified ? 0.33f : 0f) +
            (thinkingCertified ? 0.33f : 0f) +
            (decisionConfident ? 0.34f : 0f);

        float coherenceScore =
            (feeling.GateDecision.Scores.Coherence +
             (thinking.TopThought?.Scores.Coherence ?? 0.5f) +
             decision.Confidence) / 3f;

        return new CycleQuality
        {
            IsFullyCertified = fullyCertified,
            IsPartialCertified = partialCertified,
            CertificationScore = certificationScore,
            CoherenceScore = coherenceScore,
            FeelingCertified = feelingCertified,
            ThinkingCertified = thinkingCertified,
            DecisionConfident = decisionConfident
        };
    }

    private void UpdateTrustState(CycleQuality quality)
    {
        // Update consecutive counters
        if (quality.IsFullyCertified)
        {
            _consecutiveCertifiedCycles++;
            _consecutiveFailedCycles = 0;
        }
        else if (!quality.IsPartialCertified)
        {
            _consecutiveFailedCycles++;
            _consecutiveCertifiedCycles = 0;
        }
        else
        {
            _consecutiveCertifiedCycles = Math.Max(0, _consecutiveCertifiedCycles - 1);
            _consecutiveFailedCycles = Math.Max(0, _consecutiveFailedCycles - 1);
        }

        // Update system trust
        if (quality.IsFullyCertified)
            _systemTrust = Math.Min(1f, _systemTrust + 0.02f);
        else if (!quality.IsPartialCertified)
            _systemTrust = Math.Max(0.1f, _systemTrust - 0.05f);
        else
            _systemTrust *= 0.99f;

        // Update stability based on certification consistency
        float targetStability = _consecutiveCertifiedCycles >= 5 ? 0.9f :
                               _consecutiveFailedCycles >= 3 ? 0.3f : 0.6f;
        _systemStability = _systemStability * 0.95f + targetStability * 0.05f;
    }

    private void UpdateStabilityFromOutcomes()
    {
        if (_outcomes.Count < 10) return;

        var recent = _outcomes.TakeLast(10).ToList();
        float successRate = recent.Count(o => o.Success) / (float)recent.Count;

        _systemStability = _systemStability * 0.9f + successRate * 0.1f;
    }

    private ActionRecommendation GetRecommendedAction(
        DecisionProposal decision,
        ThinkingResult thinking)
    {
        // If decision is confident, use it
        if (decision.Confidence > 0.6f)
        {
            return new ActionRecommendation
            {
                Action = decision.Type,
                Source = RecommendationSource.Decision,
                Confidence = decision.Confidence * _systemTrust,
                Target = decision.Target,
                Reasoning = decision.Reasoning,
                IsSigned = _systemTrust > 0.7f && decision.Confidence > 0.7f
            };
        }

        // If thinking has a certified plan, use it
        var planThought = thinking.CertifiedThoughts
            .FirstOrDefault(t => t.Type == ThoughtType.Plan && t.ProposedAction.HasValue);

        if (planThought != null)
        {
            return new ActionRecommendation
            {
                Action = planThought.ProposedAction!.Value,
                Source = RecommendationSource.Thinking,
                Confidence = planThought.AuthorityScore * _systemTrust,
                Reasoning = planThought.Content,
                IsSigned = _systemTrust > 0.7f
            };
        }

        // Fall back to observation
        return new ActionRecommendation
        {
            Action = DecisionType.Observe,
            Source = RecommendationSource.Fallback,
            Confidence = 0.5f,
            Reasoning = "No confident recommendation - observing",
            IsSigned = false
        };
    }

    private static List<ProbeAction> CollectProbeActions(
        FeelingResult feeling,
        ThinkingResult thinking)
    {
        var probes = new List<ProbeAction>();

        if (feeling.ProbeAction.HasValue)
            probes.Add(feeling.ProbeAction.Value);

        foreach (var result in thinking.Results.Where(r => r.ProbeAction.HasValue))
            probes.Add(result.ProbeAction!.Value);

        return probes;
    }

    private void DecayAll()
    {
        _feeling.Decay();
        _thinking.Decay();
        _knowledge.Decay();

        // Decay current state references
        if (_currentFeeling != null)
        {
            _currentFeeling.FramesSinceCertified++;
            if (_currentFeeling.FramesSinceCertified > 60)
                _currentFeeling = null;
        }

        if (_currentThought != null)
        {
            _currentThought.FramesSinceCertified++;
            if (_currentThought.FramesSinceCertified > 90)
                _currentThought = null;
        }
    }

    /// <summary>
    /// Reset all pipelines.
    /// </summary>
    public void Reset()
    {
        _currentFeeling = null;
        _currentThought = null;
        _currentDecision = null;
        _currentPercepts.Clear();
        _systemTrust = 0.5f;
        _systemStability = 0.5f;
        _consecutiveCertifiedCycles = 0;
        _consecutiveFailedCycles = 0;
        _outcomes.Clear();
    }

    public string GetDiagnostics()
    {
        return $"""
            === COGNITIVE TRUST ORCHESTRATOR ===
            Cycles: {_totalCycles}
            Full Certification Rate: {FullCertificationRate:P0}
            System Trust: {_systemTrust:F2}
            System Stability: {_systemStability:F2}
            Consecutive Certified: {_consecutiveCertifiedCycles}
            Consecutive Failed: {_consecutiveFailedCycles}

            Current State:
              Feeling: {_currentFeeling?.Type.ToString() ?? "none"} (auth={_currentFeeling?.AuthorityScore:F2})
              Thought: {_currentThought?.Content ?? "none"} (auth={_currentThought?.AuthorityScore:F2})
              Decision: {_currentDecision?.Type.ToString() ?? "none"} (conf={_currentDecision?.Confidence:F2})

            {_feeling.GetDiagnostics()}
            {_thinking.GetDiagnostics()}
            {_decision.GetDiagnostics()}
            {_knowledge.GetDiagnostics()}
            =====================================
            """;
    }
}

// ============== INPUT/OUTPUT TYPES ==============

/// <summary>
/// Input to the orchestrator.
/// </summary>
public readonly struct OrchestratorInput
{
    public long FrameId { get; init; }
    public List<PerceptionInput> Perceptions { get; init; }
    public float ThreatLevel { get; init; }
    public float OpportunityLevel { get; init; }
    public int ThreatCount { get; init; }
    public int OpportunityCount { get; init; }
    public float Health { get; init; }
    public float Ammo { get; init; }
    public float HealthChange { get; init; }
    public float SystemStrain { get; init; }
    public float ConflictingSignals { get; init; }
    public float Urgency { get; init; }
    public bool UnderAttack { get; init; }
    public bool RecentSuccess { get; init; }
    public bool RecentFailure { get; init; }
    public bool HasBodySignals { get; init; }
    public string SituationTag { get; init; }
    public List<Goal> Goals { get; init; }
}

/// <summary>
/// Perception input.
/// </summary>
public readonly struct PerceptionInput
{
    public int Id { get; init; }
    public string Label { get; init; }
    public float Confidence { get; init; }
    public float TrustScore { get; init; }
    public bool IsThreat { get; init; }
    public bool IsOpportunity { get; init; }
}

/// <summary>
/// Action outcome input for learning.
/// </summary>
public readonly struct ActionOutcomeInput
{
    public bool Success { get; init; }
    public float Reward { get; init; }
    public float Risk { get; init; }
    public float Impact { get; init; }
    public int? KnowledgeId { get; init; }
}

/// <summary>
/// Result from orchestrator.
/// </summary>
public readonly struct OrchestratorResult
{
    public long FrameId { get; init; }
    public List<CertifiedPercept> CertifiedPercepts { get; init; }
    public FeelingResult FeelingResult { get; init; }
    public ThinkingResult ThinkingResult { get; init; }
    public DecisionProposal DecisionResult { get; init; }
    public ActionRecommendation RecommendedAction { get; init; }
    public CycleQuality CycleQuality { get; init; }
    public float SystemTrust { get; init; }
    public float SystemStability { get; init; }
    public bool IsFullyCertified { get; init; }
    public List<ProbeAction> ProbeActions { get; init; }
}

/// <summary>
/// Quality of a cognitive cycle.
/// </summary>
public readonly struct CycleQuality
{
    public bool IsFullyCertified { get; init; }
    public bool IsPartialCertified { get; init; }
    public float CertificationScore { get; init; }
    public float CoherenceScore { get; init; }
    public bool FeelingCertified { get; init; }
    public bool ThinkingCertified { get; init; }
    public bool DecisionConfident { get; init; }
}

/// <summary>
/// Recommended action from orchestrator.
/// </summary>
public readonly struct ActionRecommendation
{
    public DecisionType Action { get; init; }
    public RecommendationSource Source { get; init; }
    public float Confidence { get; init; }
    public int? Target { get; init; }
    public string Reasoning { get; init; }
    public bool IsSigned { get; init; }
}

/// <summary>
/// Source of action recommendation.
/// </summary>
public enum RecommendationSource
{
    Decision,   // From decision pipeline
    Thinking,   // From thinking pipeline (plan)
    Feeling,    // From feeling pipeline (reactive)
    Knowledge,  // From retrieved knowledge
    Fallback    // Default fallback
}

/// <summary>
/// Record of cycle outcome.
/// </summary>
internal readonly struct CycleOutcome
{
    public long FrameId { get; init; }
    public bool Success { get; init; }
    public float Reward { get; init; }
    public float Risk { get; init; }
}
