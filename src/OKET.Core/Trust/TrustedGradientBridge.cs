using OKET.Core.Cognition;
using OKET.Core.Detection;
using OKET.Core.Gradients;
using OKET.Core.Operators;

namespace OKET.Core.Trust;

/// <summary>
/// Bridge between Trusted Gradient System and Cognitive Controller.
///
/// ARCHITECTURE (Hardware Trust Stack → Cognitive Stack):
///
///   [Raw Frame / GameState]
///         ↓
///   [GameStateFrameAdapter] → Convert to FrameData
///         ↓
///   [TrustedGradientSystem] ←→ Root Invariants (ROM)
///         ↓                 ←→ GradientStabilizer (Secure Enclave)
///                           ←→ PrototypeVault (Key Store)
///                           ←→ TokenAuthorizationChain (Certificate Chain)
///                           ←→ ActionSigner (Signing Authority)
///         ↓
///   [TrustedGradientBridge] ←→ ThoughtManager (mapping)
///         ↓
///   [CognitiveController] ← Uses trust scores for decision weighting
///         ↓
///   [Signed Action Output]
///
/// CORE PRINCIPLE:
/// Certifications are overrides once confirmed to work.
/// Trust flows through the chain, never around it.
/// </summary>
public sealed class TrustedGradientBridge
{
    private readonly TrustedGradientSystem _trustedSystem;
    private readonly GameStateFrameAdapter _frameAdapter;
    private readonly GatePipeline _gatePipeline;

    // Mapping between trusted tokens and detection IDs
    private readonly Dictionary<int, int> _tokenToDetection = new();
    private readonly Dictionary<int, int> _detectionToToken = new();
    private int _nextDetectionId = 2000; // High to avoid conflicts

    // Last result for feedback
    private TrustedPipelineResult _lastResult;
    private ActionId _lastAction;

    // Statistics
    private int _totalCycles;
    private int _tokensProcessed;
    private int _tokensAuthorized;
    private int _actionsSigned;
    private float _avgTrustLevel;

    public TrustedGradientSystem TrustedSystem => _trustedSystem;
    public GatePipeline GatePipeline => _gatePipeline;
    public TrustedPipelineResult LastResult => _lastResult;
    public int TotalCycles => _totalCycles;
    public float AuthorizationRate => _tokensProcessed > 0
        ? (float)_tokensAuthorized / _tokensProcessed
        : 0;

    public TrustedGradientBridge(int frameWidth = 1920, int frameHeight = 1080)
    {
        _trustedSystem = new TrustedGradientSystem(frameWidth, frameHeight);
        _frameAdapter = new GameStateFrameAdapter(frameWidth, frameHeight);
        _gatePipeline = new GatePipeline();
    }

    /// <summary>
    /// Process game state through the full trusted pipeline.
    /// Returns trusted detections and action recommendation.
    /// </summary>
    public TrustedBridgeResult Process(
        State.GameState gameState,
        Audio.AudioSnapshot audioSnapshot,
        ThoughtManager thoughtManager,
        float systemStrain)
    {
        _totalCycles++;

        // === STAGE 1: ADAPT GAME STATE TO FRAME DATA ===
        _frameAdapter.Update(gameState, audioSnapshot);

        // === STAGE 2: PROCESS THROUGH TRUSTED GRADIENT SYSTEM ===
        float health = gameState.Hud.Health / 100f;
        float threatLevel = ComputeThreatLevel(gameState, audioSnapshot);
        bool isUrgent = threatLevel > 0.7f || health < 0.3f;

        _lastResult = _trustedSystem.ProcessFrame(
            _frameAdapter,
            health,
            threatLevel,
            isUrgent);

        // === STAGE 3: PROCESS THROUGH PIPELINE GATES ===
        var gatedResult = ProcessThroughGates(_lastResult, threatLevel);

        // === STAGE 4: MAP TRUSTED TOKENS TO DETECTIONS ===
        var trustedDetections = MapToDetections(_lastResult.Tokens);
        _tokensProcessed += trustedDetections.Count;
        _tokensAuthorized += trustedDetections.Count(d => d.IsAuthorized);

        // === STAGE 5: SYNC WITH THOUGHT MANAGER ===
        SyncWithThoughts(thoughtManager, trustedDetections, _lastResult);

        // === STAGE 6: GET UNIFIED RECOMMENDATION ===
        var recommendation = GetUnifiedRecommendation(
            _lastResult,
            thoughtManager,
            threatLevel,
            systemStrain);

        // Track action
        if (_lastResult.SigningResult?.IsSigned == true)
        {
            _lastAction = _lastResult.SigningResult.Value.SignedAction;
            _actionsSigned++;
        }

        // Update average trust
        if (trustedDetections.Any(d => d.IsAuthorized))
        {
            float avgTrust = trustedDetections
                .Where(d => d.IsAuthorized)
                .Average(d => d.TrustLevel);
            _avgTrustLevel = _avgTrustLevel * 0.95f + avgTrust * 0.05f;
        }

        return new TrustedBridgeResult
        {
            TrustedResult = _lastResult,
            GatedResult = gatedResult,
            TrustedDetections = trustedDetections,
            Recommendation = recommendation,
            PipelineGain = _gatePipeline.PipelineGain,
            IsStable = _gatePipeline.IsStable,
            AverageTrust = _avgTrustLevel
        };
    }

    /// <summary>
    /// Compute threat level from game state and audio.
    /// </summary>
    private static float ComputeThreatLevel(State.GameState gameState, Audio.AudioSnapshot audio)
    {
        float baseThreat = gameState.Detections.ThreatCount / 5f; // 5 threats = max
        float audioThreat = audio.HasThreatSounds ? 0.2f : 0f;
        float damageThreat = audio.HasDamageSounds ? 0.3f : 0f;
        float healthThreat = gameState.Hud.Health < 30 ? 0.3f : 0f;

        return Math.Clamp(baseThreat + audioThreat + damageThreat + healthThreat, 0f, 1f);
    }

    /// <summary>
    /// Process through pipeline gates.
    /// </summary>
    private PipelineCycleResult ProcessThroughGates(
        TrustedPipelineResult trustedResult,
        float threatLevel)
    {
        var prepInput = new PreprocessingInput
        {
            GrayscaleStrength = 0.8f,
            ColorStrength = 0.7f,
            Quality = trustedResult.Superstate?.AverageTrust ?? 0.5f,
            ROIValues = new Dictionary<string, float>
            {
                ["Minimap"] = 0.3f,
                ["Center"] = 0.9f,
                ["HUD"] = 0.5f
            }
        };

        return _gatePipeline.ProcessCycle(prepInput, threatLevel);
    }

    /// <summary>
    /// Map trusted tokens to detection-compatible format.
    /// </summary>
    private List<TrustedDetection> MapToDetections(List<TrustedToken> tokens)
    {
        var detections = new List<TrustedDetection>();

        foreach (var token in tokens)
        {
            // Get or create detection ID
            int detectionId;
            if (_tokenToDetection.TryGetValue(token.ObjectId, out int existingId))
            {
                detectionId = existingId;
            }
            else
            {
                detectionId = _nextDetectionId++;
                _tokenToDetection[token.ObjectId] = detectionId;
                _detectionToToken[detectionId] = token.ObjectId;
            }

            // Map to detection class based on behavior and trust
            var detectionClass = MapToDetectionClass(token);

            var sig = token.GradientObject.GetSignature();
            var detection = new TrustedDetection
            {
                TrackId = detectionId,
                TokenId = token.ObjectId,
                Class = detectionClass,
                Confidence = token.GradientObject.Confidence,
                TrustLevel = token.TrustLevel,
                IsAuthorized = token.IsAuthorized,
                NormalizedX = sig.NormalizedX,
                NormalizedY = sig.NormalizedY,
                VelocityX = sig.VelocityX,
                VelocityY = sig.VelocityY,
                Area = sig.Area,
                ThreatScore = token.GradientObject.ThreatLevel(),
                OpportunityScore = token.GradientObject.OpportunityLevel(),
                PrototypeId = token.PrototypeId,
                PrototypeName = token.PrototypeName,
                StabilizationStage = token.StabilizationResult.IsStabilized
                    ? 4 // All stages passed
                    : (int)token.StabilizationResult.RejectedAt,
                RootScore = token.StabilizationResult.RootScore,
                ChainScore = token.AuthorizationResult.Scores.Chain
            };

            detections.Add(detection);
        }

        return detections;
    }

    /// <summary>
    /// Map trusted token to detection class based on learned behavior.
    /// </summary>
    private static DetectionClass MapToDetectionClass(TrustedToken token)
    {
        // Use prototype name if available
        if (token.PrototypeName != null)
        {
            if (token.PrototypeName.Contains("threat", StringComparison.OrdinalIgnoreCase) ||
                token.PrototypeName.Contains("zombie", StringComparison.OrdinalIgnoreCase))
                return DetectionClass.Zombie;

            if (token.PrototypeName.Contains("item", StringComparison.OrdinalIgnoreCase) ||
                token.PrototypeName.Contains("health", StringComparison.OrdinalIgnoreCase))
                return DetectionClass.HealthKit;

            if (token.PrototypeName.Contains("ammo", StringComparison.OrdinalIgnoreCase))
                return DetectionClass.AmmoCrate;
        }

        // Use behavior scores
        if (token.GradientObject.ThreatLevel() > 0.5f)
            return DetectionClass.Zombie;

        if (token.GradientObject.OpportunityLevel() > 0.4f)
            return DetectionClass.AmmoCrate;

        return DetectionClass.Unknown;
    }

    /// <summary>
    /// Sync trusted tokens with thought manager.
    /// </summary>
    private void SyncWithThoughts(
        ThoughtManager thoughtManager,
        List<TrustedDetection> detections,
        TrustedPipelineResult trustedResult)
    {
        foreach (var detection in detections.Where(d => d.IsAuthorized))
        {
            var thought = thoughtManager.GetThought(detection.TrackId);
            if (thought == null) continue;

            // Apply trust-based learning
            // Higher trust = more reliable learning signal
            float trustWeight = detection.TrustLevel;

            // If we have a prototype, use its learned behavior
            if (detection.PrototypeName != null)
            {
                // Strong signal from trusted prototype
                float engageSuccess = detection.ThreatScore > 0.5f
                    ? 0.7f * trustWeight  // Engage threats
                    : 0.3f * trustWeight; // Don't engage non-threats

                float approachSuccess = detection.OpportunityScore > 0.3f
                    ? 0.8f * trustWeight  // Approach opportunities
                    : 0.2f * trustWeight;

                thought.ApplyLearning(
                    encounterCount: 10, // Strong prototype signal
                    engageSuccessRate: engageSuccess,
                    ignoreSuccessRate: 1f - engageSuccess,
                    predictionConfidence: trustWeight);
            }
            else
            {
                // Novel object - probe carefully
                thought.ApplyLearning(
                    encounterCount: 1, // Weak signal
                    engageSuccessRate: 0.5f, // Uncertain
                    ignoreSuccessRate: 0.5f, // Uncertain
                    predictionConfidence: trustWeight * 0.5f);
            }
        }
    }

    /// <summary>
    /// Get unified action recommendation combining trust and thought systems.
    /// </summary>
    private TrustedActionRecommendation GetUnifiedRecommendation(
        TrustedPipelineResult trustedResult,
        ThoughtManager thoughtManager,
        float threatLevel,
        float systemStrain)
    {
        // Get thought-based recommendation
        var (thoughtAction, thoughtTarget, thoughtConf) = thoughtManager.GetRecommendedAction();

        // Get trust-based recommendation (from signed action)
        ActionId trustedAction = trustedResult.RecommendedAction;
        float trustedConf = trustedResult.SigningResult?.AuthorizationScore ?? 0.5f;
        bool isSigned = trustedResult.SigningResult?.IsSigned == true;

        // CORE PRINCIPLE: Signed actions are trusted overrides
        if (isSigned && trustedResult.IsCommitAction)
        {
            // Trusted commit action - use it with high confidence
            return new TrustedActionRecommendation
            {
                Action = MapToThoughtAction(trustedAction),
                TrustAction = trustedAction,
                Confidence = trustedConf,
                Source = TrustedRecommendationSource.TrustChainCommit,
                Target = thoughtTarget,
                IsSigned = true,
                IsCommit = true,
                Reasoning = $"Trust chain signed commit: {trustedAction}"
            };
        }

        // Probe action - lower confidence, exploratory
        if (isSigned && !trustedResult.IsCommitAction)
        {
            // Trust recommends a probe - explore but carefully
            return new TrustedActionRecommendation
            {
                Action = MapToThoughtAction(trustedAction),
                TrustAction = trustedAction,
                Confidence = trustedConf * 0.7f,
                Source = TrustedRecommendationSource.TrustChainProbe,
                Target = thoughtTarget,
                IsSigned = true,
                IsCommit = false,
                Reasoning = $"Trust chain signed probe: {trustedAction}"
            };
        }

        // No signed action - fall back to thought system
        // Weight by strain (high strain = more conservative)
        if (systemStrain > 1.0f)
        {
            // High strain - be conservative
            var conservativeAction = threatLevel > 0.5f
                ? ThoughtAction.Flee
                : ThoughtAction.Observe;

            return new TrustedActionRecommendation
            {
                Action = conservativeAction,
                TrustAction = ActionId.Observe,
                Confidence = 0.6f,
                Source = TrustedRecommendationSource.ThoughtConservative,
                Target = thoughtTarget,
                IsSigned = false,
                IsCommit = false,
                Reasoning = $"High strain ({systemStrain:F2}), conservative: {conservativeAction}"
            };
        }

        // Normal strain - use thought recommendation
        return new TrustedActionRecommendation
        {
            Action = thoughtAction,
            TrustAction = MapToActionId(thoughtAction),
            Confidence = thoughtConf * 0.8f,
            Source = TrustedRecommendationSource.Thought,
            Target = thoughtTarget,
            IsSigned = false,
            IsCommit = false,
            Reasoning = $"Using thought system: {thoughtAction}"
        };
    }

    /// <summary>
    /// Map ActionId to ThoughtAction.
    /// </summary>
    private static ThoughtAction MapToThoughtAction(ActionId action)
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
            _ => ThoughtAction.Observe
        };
    }

    /// <summary>
    /// Map ThoughtAction to ActionId.
    /// </summary>
    private static ActionId MapToActionId(ThoughtAction action)
    {
        return action switch
        {
            ThoughtAction.Observe => ActionId.Observe,
            ThoughtAction.Engage => ActionId.Engage,
            ThoughtAction.Flee => ActionId.Flee,
            ThoughtAction.Approach => ActionId.Approach,
            ThoughtAction.Interact => ActionId.Interact,
            ThoughtAction.Ignore => ActionId.Observe,
            _ => ActionId.Observe
        };
    }

    /// <summary>
    /// Record outcome back to trusted system.
    /// </summary>
    public void RecordOutcome(
        bool success,
        float reward,
        float risk,
        float infoGain)
    {
        // Record to trusted gradient system
        _trustedSystem.RecordOutcome(_lastAction, success, reward, risk, infoGain);

        // Backpropagate through pipeline gates
        float errorSignal = reward < 0 ? Math.Abs(reward) : 0;
        _gatePipeline.Backpropagate(success ? 1f : 0f, errorSignal);
    }

    /// <summary>
    /// Get detection ID by token ID.
    /// </summary>
    public int? GetDetectionId(int tokenId)
    {
        return _tokenToDetection.TryGetValue(tokenId, out int id) ? id : null;
    }

    /// <summary>
    /// Get token ID by detection ID.
    /// </summary>
    public int? GetTokenId(int detectionId)
    {
        return _detectionToToken.TryGetValue(detectionId, out int id) ? id : null;
    }

    /// <summary>
    /// Reset bridge state.
    /// </summary>
    public void Reset()
    {
        _trustedSystem.Reset();
        _tokenToDetection.Clear();
        _detectionToToken.Clear();
        _lastAction = ActionId.Observe;
    }

    /// <summary>
    /// Get diagnostics.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"""
            === TRUSTED GRADIENT BRIDGE ===
            Cycles: {_totalCycles}
            Tokens: processed={_tokensProcessed} authorized={_tokensAuthorized}
            Authorization Rate: {AuthorizationRate:P1}
            Actions Signed: {_actionsSigned}
            Avg Trust: {_avgTrustLevel:F2}
            Pipeline Gain: {_gatePipeline.PipelineGain:F2}
            Pipeline Stable: {_gatePipeline.IsStable}

            {_trustedSystem.GetDiagnostics()}
            ================================
            """;
    }
}

/// <summary>
/// Detection derived from trusted token.
/// </summary>
public readonly struct TrustedDetection
{
    public int TrackId { get; init; }
    public int TokenId { get; init; }
    public DetectionClass Class { get; init; }
    public float Confidence { get; init; }
    public float TrustLevel { get; init; }
    public bool IsAuthorized { get; init; }
    public float NormalizedX { get; init; }
    public float NormalizedY { get; init; }
    public float VelocityX { get; init; }
    public float VelocityY { get; init; }
    public float Area { get; init; }
    public float ThreatScore { get; init; }
    public float OpportunityScore { get; init; }
    public int? PrototypeId { get; init; }
    public string? PrototypeName { get; init; }
    public int StabilizationStage { get; init; }
    public float RootScore { get; init; }
    public float ChainScore { get; init; }

    /// <summary>Convert to standard Detection for compatibility.</summary>
    public Detection.Detection ToDetection()
    {
        return new Detection.Detection
        {
            TrackId = TrackId,
            Class = Class,
            Confidence = Confidence * TrustLevel, // Weight by trust
            EstimatedDistance = (1f - NormalizedY) * 500f,
            Velocity = new Types.Vector2(VelocityX * 100f, VelocityY * 100f)
        };
    }
}

/// <summary>
/// Result from trusted bridge processing.
/// </summary>
public readonly struct TrustedBridgeResult
{
    public TrustedPipelineResult TrustedResult { get; init; }
    public PipelineCycleResult GatedResult { get; init; }
    public List<TrustedDetection> TrustedDetections { get; init; }
    public TrustedActionRecommendation Recommendation { get; init; }
    public float PipelineGain { get; init; }
    public bool IsStable { get; init; }
    public float AverageTrust { get; init; }
}

/// <summary>
/// Trusted action recommendation.
/// </summary>
public readonly struct TrustedActionRecommendation
{
    public ThoughtAction Action { get; init; }
    public ActionId TrustAction { get; init; }
    public float Confidence { get; init; }
    public TrustedRecommendationSource Source { get; init; }
    public ObjectThought? Target { get; init; }
    public bool IsSigned { get; init; }
    public bool IsCommit { get; init; }
    public string Reasoning { get; init; }
}

/// <summary>
/// Source of trusted action recommendation.
/// </summary>
public enum TrustedRecommendationSource
{
    TrustChainCommit,    // Signed commit from trust chain
    TrustChainProbe,     // Signed probe from trust chain
    Thought,             // From thought system
    ThoughtConservative, // Conservative fallback
    Combined             // Combination of sources
}
