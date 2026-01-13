using OKET.Core.Cognition;
using OKET.Core.Detection;
using OKET.Core.Gradients;
using OKET.Core.Operators;
using OKET.Core.Trust.Pipelines;

namespace OKET.Core.Trust;

/// <summary>
/// Unified Cognitive Trust Pipeline - The Complete Integration.
///
/// FULL ARCHITECTURE:
///
///   [Raw Frame / GameState]
///         ↓
///   [TrustedGradientBridge] ──→ Perception layer
///         ↓                 ←→ Root Invariants
///         ↓                 ←→ Stabilizer (3-stage rejection)
///         ↓                 ←→ Prototype Vault
///         ↓                 ←→ Token Authorization Chain
///         ↓
///   [CognitiveTrustOrchestrator] ──→ Cognitive layer
///         ↓                      ←→ Feeling Pipeline
///         ↓                      ←→ Thinking Pipeline
///         ↓                      ←→ Decision Pipeline
///         ↓                      ←→ Knowledge Pipeline
///         ↓
///   [Unified Decision] ──→ Final output
///         ↓
///   [Action Execution + Outcome Recording]
///         ↓
///   [Feedback to all systems]
///
/// CORE PRINCIPLES:
/// 1. Perception trust flows through gradient validation
/// 2. Cognitive trust flows through pipeline gates
/// 3. Both must agree for action signing
/// 4. Everything decays without re-certification
/// </summary>
public sealed class UnifiedCognitivePipeline
{
    // Perception layer
    private readonly TrustedGradientBridge _gradientBridge;

    // Cognitive layer
    private readonly CognitiveTrustOrchestrator _cognitiveOrchestrator;

    // Thought manager for object-thought binding
    private readonly ThoughtManager _thoughtManager;

    // State
    private long _frameId;
    private UnifiedResult? _lastResult;
    private float _systemStrain;

    // Statistics
    private int _totalCycles;
    private int _perceptionCertified;
    private int _cognitiveCertified;
    private int _bothCertified;
    private int _actionsSigned;
    private float _avgUnifiedConfidence;

    public TrustedGradientBridge GradientBridge => _gradientBridge;
    public CognitiveTrustOrchestrator CognitiveOrchestrator => _cognitiveOrchestrator;
    public ThoughtManager ThoughtManager => _thoughtManager;
    public UnifiedResult? LastResult => _lastResult;
    public int TotalCycles => _totalCycles;
    public float BothCertifiedRate => _totalCycles > 0 ? (float)_bothCertified / _totalCycles : 0;

    public UnifiedCognitivePipeline(int frameWidth = 1920, int frameHeight = 1080)
    {
        _gradientBridge = new TrustedGradientBridge(frameWidth, frameHeight);
        _cognitiveOrchestrator = new CognitiveTrustOrchestrator();
        _thoughtManager = new ThoughtManager();
    }

    /// <summary>
    /// Process a complete cycle through both pipelines.
    /// </summary>
    public UnifiedResult Process(
        State.GameState gameState,
        Audio.AudioSnapshot audioSnapshot)
    {
        _frameId++;
        _totalCycles++;

        // Update system strain
        UpdateSystemStrain(gameState);

        // === LAYER 1: PERCEPTION (Gradient Trust) ===
        var gradientResult = _gradientBridge.Process(
            gameState,
            audioSnapshot,
            _thoughtManager,
            _systemStrain);

        bool perceptionCertified = gradientResult.TrustedDetections.Any(d => d.IsAuthorized);
        if (perceptionCertified) _perceptionCertified++;

        // === LAYER 2: COGNITION (Pipeline Trust) ===
        var orchestratorInput = BuildOrchestratorInput(gameState, audioSnapshot, gradientResult);
        var cognitiveResult = _cognitiveOrchestrator.ProcessCycle(orchestratorInput);

        bool cognitiveCertified = cognitiveResult.IsFullyCertified;
        if (cognitiveCertified) _cognitiveCertified++;

        bool bothCertified = perceptionCertified && cognitiveCertified;
        if (bothCertified) _bothCertified++;

        // === LAYER 3: UNIFIED DECISION ===
        var unifiedDecision = ComputeUnifiedDecision(
            gradientResult,
            cognitiveResult,
            bothCertified);

        // Track action signing
        if (unifiedDecision.IsSigned) _actionsSigned++;

        // Update confidence tracking
        _avgUnifiedConfidence = _avgUnifiedConfidence * 0.95f + unifiedDecision.Confidence * 0.05f;

        // Build result
        _lastResult = new UnifiedResult
        {
            FrameId = _frameId,
            GradientResult = gradientResult,
            CognitiveResult = cognitiveResult,
            UnifiedDecision = unifiedDecision,
            PerceptionCertified = perceptionCertified,
            CognitiveCertified = cognitiveCertified,
            BothCertified = bothCertified,
            SystemStrain = _systemStrain,
            SystemTrust = ComputeSystemTrust(gradientResult, cognitiveResult)
        };

        return _lastResult.Value;
    }

    /// <summary>
    /// Build orchestrator input from gradient result.
    /// </summary>
    private OrchestratorInput BuildOrchestratorInput(
        State.GameState gameState,
        Audio.AudioSnapshot audio,
        TrustedBridgeResult gradientResult)
    {
        // Map gradient tokens to perceptions
        var perceptions = gradientResult.TrustedDetections
            .Select(d => new PerceptionInput
            {
                Id = d.TrackId,
                Label = d.Class.ToString(),
                Confidence = d.Confidence,
                TrustScore = d.TrustLevel,
                IsThreat = d.ThreatScore > 0.5f,
                IsOpportunity = d.OpportunityScore > 0.3f
            })
            .ToList();

        // Compute situation tag
        string situationTag = ComputeSituationTag(gameState, gradientResult);

        return new OrchestratorInput
        {
            FrameId = _frameId,
            Perceptions = perceptions,
            ThreatLevel = gameState.Detections.ThreatCount / 5f,
            OpportunityLevel = gradientResult.TrustedDetections.Count(d => d.OpportunityScore > 0.3f) / 3f,
            ThreatCount = gameState.Detections.ThreatCount,
            OpportunityCount = gradientResult.TrustedDetections.Count(d => d.OpportunityScore > 0.3f),
            Health = gameState.Hud.Health / 100f,
            Ammo = gameState.Hud.AmmoClip / 100f,
            HealthChange = ComputeHealthChange(gameState),
            SystemStrain = _systemStrain,
            ConflictingSignals = gradientResult.TrustedDetections.Count(d => !d.IsAuthorized) / Math.Max(1f, gradientResult.TrustedDetections.Count),
            Urgency = ComputeUrgency(gameState, audio),
            UnderAttack = audio.HasDamageSounds,
            RecentSuccess = false, // Would need outcome tracking
            RecentFailure = false,
            HasBodySignals = true,
            SituationTag = situationTag,
            Goals = new List<Goal>
            {
                new() { Name = "Survive", Priority = 1f },
                new() { Name = "CollectResources", Priority = 0.5f }
            }
        };
    }

    /// <summary>
    /// Compute unified decision from both pipelines.
    /// </summary>
    private UnifiedDecision ComputeUnifiedDecision(
        TrustedBridgeResult gradient,
        OrchestratorResult cognitive,
        bool bothCertified)
    {
        // RULE: Both must certify for commit, otherwise probe
        if (bothCertified)
        {
            // Check if recommendations agree
            var gradientAction = gradient.Recommendation.TrustAction;
            var cognitiveAction = cognitive.RecommendedAction.Action.ToActionId();

            if (ActionsAgree(gradientAction, cognitiveAction))
            {
                // Strong agreement - signed commit
                return new UnifiedDecision
                {
                    Action = gradientAction,
                    Confidence = (gradient.Recommendation.Confidence + cognitive.RecommendedAction.Confidence) / 2f,
                    Source = UnifiedDecisionSource.BothAgree,
                    IsSigned = true,
                    IsCommit = true,
                    Reasoning = $"Both pipelines agree: {gradientAction}"
                };
            }

            // Disagreement - use higher confidence, but as probe
            if (gradient.Recommendation.Confidence > cognitive.RecommendedAction.Confidence)
            {
                return new UnifiedDecision
                {
                    Action = gradientAction,
                    Confidence = gradient.Recommendation.Confidence * 0.8f,
                    Source = UnifiedDecisionSource.GradientPreferred,
                    IsSigned = true,
                    IsCommit = false, // Probe due to disagreement
                    Reasoning = $"Gradient preferred (disagreement): {gradientAction} vs {cognitiveAction}"
                };
            }

            return new UnifiedDecision
            {
                Action = cognitiveAction,
                Confidence = cognitive.RecommendedAction.Confidence * 0.8f,
                Source = UnifiedDecisionSource.CognitivePreferred,
                IsSigned = true,
                IsCommit = false,
                Reasoning = $"Cognitive preferred (disagreement): {cognitiveAction} vs {gradientAction}"
            };
        }

        // Only one certified - probe at best
        if (gradient.Recommendation.IsSigned && !cognitive.IsFullyCertified)
        {
            return new UnifiedDecision
            {
                Action = gradient.Recommendation.TrustAction,
                Confidence = gradient.Recommendation.Confidence * 0.6f,
                Source = UnifiedDecisionSource.GradientOnly,
                IsSigned = true,
                IsCommit = false,
                Reasoning = "Only gradient certified - probe"
            };
        }

        if (!gradient.Recommendation.IsSigned && cognitive.IsFullyCertified)
        {
            return new UnifiedDecision
            {
                Action = cognitive.RecommendedAction.Action.ToActionId(),
                Confidence = cognitive.RecommendedAction.Confidence * 0.6f,
                Source = UnifiedDecisionSource.CognitiveOnly,
                IsSigned = true,
                IsCommit = false,
                Reasoning = "Only cognitive certified - probe"
            };
        }

        // Neither certified - observe
        return new UnifiedDecision
        {
            Action = ActionId.Observe,
            Confidence = 0.4f,
            Source = UnifiedDecisionSource.Fallback,
            IsSigned = false,
            IsCommit = false,
            Reasoning = "Neither pipeline certified - observing"
        };
    }

    /// <summary>
    /// Check if two actions agree (compatible).
    /// </summary>
    private static bool ActionsAgree(ActionId a, ActionId b)
    {
        if (a == b) return true;

        // Compatible pairs
        return (a, b) switch
        {
            (ActionId.Engage, ActionId.Kite) => true,
            (ActionId.Kite, ActionId.Engage) => true,
            (ActionId.Approach, ActionId.Interact) => true,
            (ActionId.Interact, ActionId.Approach) => true,
            (ActionId.Observe, ActionId.Probe) => true,
            (ActionId.Probe, ActionId.Observe) => true,
            _ => false
        };
    }

    /// <summary>
    /// Compute overall system trust.
    /// </summary>
    private static float ComputeSystemTrust(
        TrustedBridgeResult gradient,
        OrchestratorResult cognitive)
    {
        float gradientTrust = gradient.AverageTrust;
        float cognitiveTrust = cognitive.SystemTrust;

        // Geometric mean - both must be good
        return MathF.Sqrt(gradientTrust * cognitiveTrust);
    }

    /// <summary>
    /// Update system strain.
    /// </summary>
    private void UpdateSystemStrain(State.GameState gameState)
    {
        // Strain increases with threats, decreases with time
        float threatStrain = gameState.Detections.ThreatCount * 0.2f;
        float healthStrain = gameState.Hud.Health < 30 ? 0.3f : 0f;

        float targetStrain = threatStrain + healthStrain;
        _systemStrain = _systemStrain * 0.95f + targetStrain * 0.05f;
    }

    private float _lastHealth;
    private float ComputeHealthChange(State.GameState gameState)
    {
        float change = gameState.Hud.Health - _lastHealth;
        _lastHealth = gameState.Hud.Health;
        return change / 100f;
    }

    private static float ComputeUrgency(State.GameState gameState, Audio.AudioSnapshot audio)
    {
        float threatUrgency = gameState.Detections.ThreatCount > 2 ? 0.5f : 0f;
        float audioUrgency = audio.HasDamageSounds ? 0.3f : 0f;
        float healthUrgency = gameState.Hud.Health < 30 ? 0.4f : 0f;

        return Math.Clamp(threatUrgency + audioUrgency + healthUrgency, 0f, 1f);
    }

    private static string ComputeSituationTag(State.GameState gameState, TrustedBridgeResult gradient)
    {
        if (gameState.Detections.ThreatCount > 2)
            return "MultiThreat";
        if (gameState.Detections.ThreatCount > 0)
            return "ThreatPresent";
        if (gradient.TrustedDetections.Any(d => d.OpportunityScore > 0.5f))
            return "Opportunity";
        return "Clear";
    }

    private static ThoughtAction MapActionIdToThoughtAction(ActionId action)
    {
        return action switch
        {
            ActionId.Observe => ThoughtAction.Observe,
            ActionId.Engage => ThoughtAction.Engage,
            ActionId.Flee => ThoughtAction.Flee,
            ActionId.Kite => ThoughtAction.Engage,
            ActionId.Approach => ThoughtAction.Approach,
            ActionId.Interact => ThoughtAction.Interact,
            ActionId.Probe => ThoughtAction.Observe,
            ActionId.Ignore => ThoughtAction.Ignore,
            _ => ThoughtAction.Observe
        };
    }

    /// <summary>
    /// Record outcome of action taken.
    /// </summary>
    public void RecordOutcome(
        bool success,
        float reward,
        float risk,
        float infoGain)
    {
        // Record to gradient bridge
        _gradientBridge.RecordOutcome(success, reward, risk, infoGain);

        // Record to cognitive orchestrator
        _cognitiveOrchestrator.RecordOutcome(new ActionOutcomeInput
        {
            Success = success,
            Reward = reward,
            Risk = risk,
            Impact = Math.Abs(reward)
        });

        // Record to thought manager for most urgent thought
        var mostUrgent = _thoughtManager.MostUrgent;
        if (mostUrgent != null)
        {
            var thoughtAction = MapActionIdToThoughtAction(_lastResult?.UnifiedDecision.Action ?? ActionId.Observe);
            _thoughtManager.RecordOutcome(mostUrgent, thoughtAction, success ? reward : -reward);
        }
    }

    /// <summary>
    /// Reset all systems.
    /// </summary>
    public void Reset()
    {
        _gradientBridge.Reset();
        _cognitiveOrchestrator.Reset();
        _thoughtManager.Reset();
        _lastResult = null;
        _systemStrain = 0;
    }

    public string GetDiagnostics()
    {
        return $"""
            === UNIFIED COGNITIVE PIPELINE ===
            Cycles: {_totalCycles}
            Certified: perception={_perceptionCertified} cognitive={_cognitiveCertified} both={_bothCertified}
            Both Certified Rate: {BothCertifiedRate:P1}
            Actions Signed: {_actionsSigned}
            Avg Unified Confidence: {_avgUnifiedConfidence:F2}
            System Strain: {_systemStrain:F2}

            {_gradientBridge.GetDiagnostics()}

            {_cognitiveOrchestrator.GetDiagnostics()}
            ==================================
            """;
    }
}

/// <summary>
/// Result from unified pipeline.
/// </summary>
public readonly struct UnifiedResult
{
    public long FrameId { get; init; }
    public TrustedBridgeResult GradientResult { get; init; }
    public OrchestratorResult CognitiveResult { get; init; }
    public UnifiedDecision UnifiedDecision { get; init; }
    public bool PerceptionCertified { get; init; }
    public bool CognitiveCertified { get; init; }
    public bool BothCertified { get; init; }
    public float SystemStrain { get; init; }
    public float SystemTrust { get; init; }
}

/// <summary>
/// Unified decision from both pipelines.
/// </summary>
public readonly struct UnifiedDecision
{
    public ActionId Action { get; init; }
    public float Confidence { get; init; }
    public UnifiedDecisionSource Source { get; init; }
    public bool IsSigned { get; init; }
    public bool IsCommit { get; init; }
    public string Reasoning { get; init; }
}

/// <summary>
/// Source of unified decision.
/// </summary>
public enum UnifiedDecisionSource
{
    BothAgree,           // Both pipelines certified and agree
    GradientPreferred,   // Both certified but disagree, gradient wins
    CognitivePreferred,  // Both certified but disagree, cognitive wins
    GradientOnly,        // Only gradient certified
    CognitiveOnly,       // Only cognitive certified
    Fallback             // Neither certified
}
