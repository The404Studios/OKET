using OKET.Core.Gradients;

namespace OKET.Core.Trust;

/// <summary>
/// Trusted Gradient System - The Master Integration.
///
/// COMPLETE PIPELINE:
///
///   [Raw Frame]
///        ↓
///   [GradientField] ──→ Local perception fields
///        ↓
///   [GradientStabilizer] ──→ Secure enclave validation (Stage A/B/C)
///        ↓
///   [GradientObject] ──→ Coherent regional perception
///        ↓
///   [PrototypeVault] ──→ Signature matching + stabilize-then-name
///        ↓
///   [SignatureToken] ──→ Tokenized representation
///        ↓
///   [TokenAuthorizationChain] ──→ Full chain validation
///        ↓
///   [Superstate] ──→ Global scene graph
///        ↓
///   [ActionSigner] ──→ Signed action (commit or probe)
///        ↓
///   [TransitionMemory] ──→ Causal learning
///        ↓
///   [Outcome Recording] ──→ Feedback to all systems
///
/// CORE PRINCIPLE:
/// No action without validated gradient chain rooted in invariant perception.
/// Certifications are overrides once confirmed to work.
/// </summary>
public sealed class TrustedGradientSystem
{
    // Perception layer
    private readonly GradientField _field;
    private readonly GradientObjectTracker _tracker;

    // Trust layer
    private readonly GradientStabilizer _stabilizer;
    private readonly PrototypeVault _vault;
    private readonly TokenAuthorizationChain _authChain;
    private readonly ActionSigner _signer;

    // Memory layer
    private readonly TransitionMemory _memory;

    // State
    private readonly List<TrustedToken> _currentTokens = new();
    private TrustedSuperstate? _currentSuperstate;
    private TrustedSuperstate? _previousSuperstate;
    private SigningResult? _lastSigningResult;
    private ActionId _lastAction;

    // Frame tracking
    private long _frameId;
    private int _superstateIdCounter;

    // Statistics
    private int _totalFrames;
    private int _objectsDetected;
    private int _objectsStabilized;
    private int _objectsAuthorized;
    private int _actionsSigned;
    private int _probesIssued;

    public GradientField Field => _field;
    public GradientStabilizer Stabilizer => _stabilizer;
    public PrototypeVault Vault => _vault;
    public TokenAuthorizationChain AuthChain => _authChain;
    public ActionSigner Signer => _signer;
    public TransitionMemory Memory => _memory;
    public IReadOnlyList<TrustedToken> CurrentTokens => _currentTokens;
    public TrustedSuperstate? CurrentSuperstate => _currentSuperstate;
    public SigningResult? LastSigningResult => _lastSigningResult;
    public int TotalFrames => _totalFrames;
    public float AuthorizationRate => _objectsDetected > 0
        ? (float)_objectsAuthorized / _objectsDetected
        : 0;

    public TrustedGradientSystem(int frameWidth, int frameHeight, int cellSize = 16)
    {
        // Initialize perception
        _field = new GradientField(frameWidth, frameHeight, cellSize);
        _tracker = new GradientObjectTracker();

        // Initialize trust (creates its own vault and auth chain)
        _stabilizer = new GradientStabilizer();
        _vault = new PrototypeVault();
        _authChain = new TokenAuthorizationChain();
        _signer = new ActionSigner(_authChain);

        // Initialize memory
        _memory = new TransitionMemory();
    }

    /// <summary>
    /// Process a frame through the complete trusted pipeline.
    /// </summary>
    public TrustedPipelineResult ProcessFrame(
        FrameData frame,
        float health,
        float threatLevel,
        bool isUrgent)
    {
        _frameId++;
        _totalFrames++;

        // Store previous superstate for transition recording
        _previousSuperstate = _currentSuperstate;

        // === STAGE 1: LOCAL PERCEPTION (GradientField) ===
        _field.Update(frame, _frameId);

        // === STAGE 2: REGIONAL PERCEPTION (GradientObjects) ===
        var objects = _tracker.Update(_field, _frameId);
        _objectsDetected += objects.Count;

        // === STAGE 3: STABILIZATION + TRUST CHAIN ===
        _currentTokens.Clear();

        foreach (var obj in objects)
        {
            // Create stabilization input from gradient object
            var stabInput = CreateStabilizationInput(obj);
            var stabResult = _stabilizer.Stabilize(stabInput, _frameId);

            if (!stabResult.IsStabilized)
            {
                // Object rejected by stabilizer - skip
                continue;
            }

            _objectsStabilized++;

            // Create token authorization input
            var authInput = CreateAuthorizationInput(obj, stabResult, health, threatLevel);
            var authResult = _authChain.Authorize(authInput, _frameId);

            // Create trusted token
            var trustedToken = new TrustedToken
            {
                ObjectId = obj.ObjectId,
                GradientObject = obj,
                StabilizationResult = stabResult,
                AuthorizationResult = authResult,
                IsAuthorized = authResult.IsAuthorized,
                TrustLevel = authResult.IsAuthorized
                    ? authResult.AuthorizationScore
                    : 0,
                PrototypeId = authResult.MatchedPrototypeId,
                PrototypeName = authResult.PrototypeName
            };

            _currentTokens.Add(trustedToken);

            if (authResult.IsAuthorized)
            {
                _objectsAuthorized++;
            }
        }

        // === STAGE 4: BUILD TRUSTED SUPERSTATE ===
        _currentSuperstate = BuildTrustedSuperstate(_currentTokens, threatLevel);

        // === STAGE 5: ACTION SIGNING ===
        SigningResult? signingResult = null;
        if (_currentTokens.Any(t => t.IsAuthorized))
        {
            // Find best token to act on
            var bestToken = _currentTokens
                .Where(t => t.IsAuthorized)
                .OrderByDescending(t => t.TrustLevel * (t.GradientObject.ThreatLevel() + 0.1f))
                .FirstOrDefault();

            if (bestToken != null)
            {
                var proposedAction = DetermineProposedAction(bestToken, _currentSuperstate, threatLevel);
                var signingRequest = new SigningRequest
                {
                    TokenInput = CreateAuthorizationInput(
                        bestToken.GradientObject,
                        bestToken.StabilizationResult,
                        health,
                        threatLevel),
                    ProposedAction = proposedAction,
                    ThreatLevel = threatLevel,
                    Health = health,
                    IsUrgent = isUrgent
                };

                signingResult = _signer.RequestSigning(signingRequest, _frameId);
                _lastSigningResult = signingResult;

                if (signingResult.Value.IsSigned)
                {
                    if (signingResult.Value.IsCommit)
                        _actionsSigned++;
                    else
                        _probesIssued++;

                    _lastAction = signingResult.Value.SignedAction;
                }
            }
        }

        // === STAGE 6: CLEANUP ===
        if (_frameId % 100 == 0)
        {
            _stabilizer.Cleanup(_frameId);
            _authChain.Cleanup(_frameId);
            _vault.Decay(_frameId);
        }

        return new TrustedPipelineResult
        {
            FrameId = _frameId,
            ObjectsDetected = objects.Count,
            ObjectsStabilized = _currentTokens.Count(t => t.StabilizationResult.IsStabilized),
            ObjectsAuthorized = _currentTokens.Count(t => t.IsAuthorized),
            Tokens = _currentTokens.ToList(),
            Superstate = _currentSuperstate,
            SigningResult = signingResult,
            RecommendedAction = signingResult?.IsSigned == true
                ? signingResult.Value.SignedAction
                : ActionId.Observe,
            IsCommitAction = signingResult?.IsCommit ?? false
        };
    }

    /// <summary>
    /// Record outcome of action taken.
    /// </summary>
    public void RecordOutcome(
        ActionId actionTaken,
        bool success,
        float reward,
        float risk,
        float infoGain)
    {
        // Record to transition memory
        if (_previousSuperstate != null && _currentSuperstate != null)
        {
            _memory.RecordTransition(
                _previousSuperstate.Signature,
                MapToMemoryAction(actionTaken),
                _currentSuperstate.Signature,
                new TransitionOutcome
                {
                    Success = reward,
                    Risk = risk,
                    InfoGain = infoGain,
                    Improved = reward > 0,
                    Survived = success
                },
                _currentSuperstate.AverageNovelty);
        }

        // Record to signer if we have a signature
        if (_lastSigningResult?.Signature != null)
        {
            _signer.RecordOutcome(
                _lastSigningResult.Value.Signature.Value,
                new ActionOutcome
                {
                    Success = success,
                    Reward = reward,
                    Risk = risk,
                    InfoGain = infoGain
                });
        }

        // Record to auth chain for tokens involved
        foreach (var token in _currentTokens.Where(t => t.IsAuthorized))
        {
            _authChain.RecordOutcome(
                token.ObjectId,
                actionTaken,
                success,
                reward,
                risk);
        }
    }

    /// <summary>
    /// Create stabilization input from gradient object.
    /// </summary>
    private StabilizationInput CreateStabilizationInput(GradientObject obj)
    {
        var sig = obj.GetSignature();
        return new StabilizationInput
        {
            ObjectId = obj.ObjectId,
            SegmentationQuality = obj.Confidence,
            AreaNorm = sig.Area / 1000f, // Normalize
            SignalToNoise = obj.Confidence * 0.8f + 0.2f,
            MotionCoherence = obj.Speed > 0.05f ? 0.7f : 1.0f, // Simplified
            EdgeDensity = sig.EdgeDensity,
            TemporalStability = sig.Stability,
            IsMoving = obj.Speed > 0.05f,
            IsFlashEvent = obj.AgeFrames < 3,
            ColorAgreement = 0.7f, // Simplified
            Saturation = sig.Saturation,
            Value = sig.Value,
            SignatureDrift = sig.Jitter,
            HudOverlap = sig.NormalizedY < 0.1f || sig.NormalizedY > 0.9f ? 0.3f : 0f,
            IsStatic = obj.Speed < 0.02f,
            CenterX = sig.NormalizedX,
            CenterY = sig.NormalizedY,
            CameraMotionBias = 0,
            Speed = obj.Speed,
            Jitter = sig.Jitter
        };
    }

    /// <summary>
    /// Create authorization input from gradient object.
    /// </summary>
    private TokenAuthorizationInput CreateAuthorizationInput(
        GradientObject obj,
        StabilizationResult stabResult,
        float health,
        float threatLevel)
    {
        var sig = obj.GetSignature();
        return new TokenAuthorizationInput
        {
            TokenId = obj.ObjectId,
            SegmentationQuality = obj.Confidence,
            AreaNorm = sig.Area / 1000f,
            SignalToNoise = obj.Confidence * 0.8f + 0.2f,
            MotionCoherence = obj.Speed > 0.05f ? 0.7f : 1.0f,
            EdgeDensity = sig.EdgeDensity,
            TemporalStability = sig.Stability,
            Speed = obj.Speed,
            Jitter = sig.Jitter,
            Persistence = stabResult.StabilityScore,
            IsFlashEvent = obj.AgeFrames < 3,
            VelocityX = sig.VelocityX,
            VelocityY = sig.VelocityY,
            Acceleration = sig.Acceleration,
            AspectRatio = sig.AspectRatio,
            Compactness = sig.Compactness,
            HueMean = sig.DominantHue,
            HueVar = sig.HueVariance,
            Saturation = sig.Saturation,
            Value = sig.Value,
            Confidence = obj.Confidence,
            CenterX = sig.NormalizedX,
            CenterY = sig.NormalizedY,
            RoiId = 0, // Center ROI
            HudOverlap = sig.NormalizedY < 0.1f || sig.NormalizedY > 0.9f ? 0.3f : 0f,
            CameraMotionBias = 0,
            SignatureDrift = sig.Jitter,
            ColorAgreement = 0.7f,
            Contrast = sig.Value,
            NoveltyScore = obj.PrototypeMatch < 0.5f ? 0.8f : 0.2f,
            ThreatLevel = threatLevel,
            Health = health
        };
    }

    /// <summary>
    /// Build trusted superstate from tokens.
    /// </summary>
    private TrustedSuperstate BuildTrustedSuperstate(
        List<TrustedToken> tokens,
        float threatLevel)
    {
        var superstate = new TrustedSuperstate
        {
            SuperstateId = _superstateIdCounter++,
            FrameId = _frameId,
            Tokens = tokens,
            TotalTokens = tokens.Count,
            AuthorizedTokens = tokens.Count(t => t.IsAuthorized),
            ThreatLevel = threatLevel,
            AverageNovelty = tokens.Count > 0
                ? tokens.Average(t => t.AuthorizationResult.IsAuthorized ? 0.2f : 0.8f)
                : 0,
            AverageTrust = tokens.Where(t => t.IsAuthorized).Select(t => t.TrustLevel).DefaultIfEmpty(0).Average()
        };

        // Compute signature for memory
        superstate.Signature = new SuperstateSignature
        {
            NodeCount = tokens.Count,
            ThreatLikeCount = tokens.Count(t => t.GradientObject.ThreatLevel() > 0.5f),
            MovingCount = tokens.Count(t => t.GradientObject.Speed > 0.05f),
            StaticCount = tokens.Count(t => t.GradientObject.Speed <= 0.05f),
            Urgency = threatLevel,
            Opportunity = tokens.Count(t => t.GradientObject.OpportunityLevel() > 0.3f) / Math.Max(1f, tokens.Count),
            MeanConfidence = superstate.AverageTrust,
            Type = ClassifySuperstate(tokens, threatLevel)
        };

        return superstate;
    }

    /// <summary>
    /// Classify superstate type.
    /// </summary>
    private static SuperstateType ClassifySuperstate(List<TrustedToken> tokens, float threatLevel)
    {
        int threats = tokens.Count(t => t.GradientObject.ThreatLevel() > 0.5f && t.IsAuthorized);
        int opportunities = tokens.Count(t => t.GradientObject.OpportunityLevel() > 0.3f && t.IsAuthorized);

        if (threatLevel > 0.7f || threats > 2)
            return SuperstateType.MultiThreat;
        if (threatLevel > 0.4f || threats > 0)
            return SuperstateType.ThreatPresent;
        if (opportunities > 1)
            return SuperstateType.OpportunityCluster;
        if (tokens.All(t => !t.IsAuthorized))
            return SuperstateType.Clear;

        return SuperstateType.Neutral;
    }

    /// <summary>
    /// Determine proposed action based on token and situation.
    /// </summary>
    private ActionId DetermineProposedAction(
        TrustedToken token,
        TrustedSuperstate superstate,
        float threatLevel)
    {
        var obj = token.GradientObject;

        // High threat - engage or flee
        if (obj.ThreatLevel() > 0.6f)
        {
            if (superstate.ThreatLevel > 0.7f)
                return ActionId.Kite; // Multiple threats - kite
            return ActionId.Engage;
        }

        // High opportunity - approach or interact
        if (obj.OpportunityLevel() > 0.5f)
        {
            if (threatLevel < 0.3f)
                return ActionId.Interact;
            return ActionId.Approach;
        }

        // Novel object - probe
        if (token.PrototypeId == null)
            return ActionId.Probe;

        return ActionId.Observe;
    }

    /// <summary>
    /// Map action to memory action type.
    /// </summary>
    private static Gradients.ActionType MapToMemoryAction(ActionId action)
    {
        return action switch
        {
            ActionId.Observe => Gradients.ActionType.Observe,
            ActionId.Engage => Gradients.ActionType.Engage,
            ActionId.Flee => Gradients.ActionType.Retreat,
            ActionId.Kite => Gradients.ActionType.Kite,
            ActionId.Approach => Gradients.ActionType.Approach,
            ActionId.Interact => Gradients.ActionType.Interact,
            ActionId.Probe => Gradients.ActionType.Explore,
            _ => Gradients.ActionType.Observe
        };
    }

    /// <summary>
    /// Reset the system.
    /// </summary>
    public void Reset()
    {
        _tracker.Reset();
        _currentTokens.Clear();
        _currentSuperstate = null;
        _previousSuperstate = null;
        _lastSigningResult = null;
    }

    /// <summary>
    /// Get comprehensive diagnostics.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"""
            === TRUSTED GRADIENT SYSTEM ===
            Frame: {_frameId}
            Objects: detected={_objectsDetected} stabilized={_objectsStabilized} authorized={_objectsAuthorized}
            Authorization Rate: {AuthorizationRate:P1}
            Actions: signed={_actionsSigned} probes={_probesIssued}

            Current Tokens: {_currentTokens.Count} ({_currentTokens.Count(t => t.IsAuthorized)} authorized)
            Superstate: {_currentSuperstate?.Signature.Type}

            {_stabilizer.GetDiagnostics()}
            {_vault.GetDiagnostics()}
            {_signer.GetDiagnostics()}
            {_memory.GetDiagnostics()}
            ================================
            """;
    }
}

/// <summary>
/// A token that has been through the full trust pipeline.
/// </summary>
public sealed class TrustedToken
{
    public int ObjectId { get; init; }
    public GradientObject GradientObject { get; init; } = null!;
    public StabilizationResult StabilizationResult { get; init; }
    public TokenAuthorizationResult AuthorizationResult { get; init; }
    public bool IsAuthorized { get; init; }
    public float TrustLevel { get; init; }
    public int? PrototypeId { get; init; }
    public string? PrototypeName { get; init; }
}

/// <summary>
/// Superstate built from trusted tokens.
/// </summary>
public sealed class TrustedSuperstate
{
    public int SuperstateId { get; init; }
    public long FrameId { get; init; }
    public List<TrustedToken> Tokens { get; init; } = new();
    public int TotalTokens { get; init; }
    public int AuthorizedTokens { get; init; }
    public float ThreatLevel { get; init; }
    public float AverageNovelty { get; init; }
    public float AverageTrust { get; init; }
    public SuperstateSignature Signature { get; set; }
}

/// <summary>
/// Result of the full trusted pipeline.
/// </summary>
public readonly struct TrustedPipelineResult
{
    public long FrameId { get; init; }
    public int ObjectsDetected { get; init; }
    public int ObjectsStabilized { get; init; }
    public int ObjectsAuthorized { get; init; }
    public List<TrustedToken> Tokens { get; init; }
    public TrustedSuperstate? Superstate { get; init; }
    public SigningResult? SigningResult { get; init; }
    public ActionId RecommendedAction { get; init; }
    public bool IsCommitAction { get; init; }
}

/// <summary>
/// Extension for GradientObject to add threat/opportunity levels.
/// </summary>
public static class GradientObjectExtensions
{
    public static float ThreatLevel(this GradientObject obj)
    {
        // Threat based on: fast, approaching, large
        float speedThreat = Math.Min(1f, obj.Speed * 2f);
        float approachThreat = obj.VelocityY > 0 ? 0.3f : 0f; // Moving down = approaching
        float sizeThreat = Math.Min(0.5f, obj.Area / 100f);

        return Math.Min(1f, speedThreat * 0.5f + approachThreat + sizeThreat);
    }

    public static float OpportunityLevel(this GradientObject obj)
    {
        // Opportunity based on: static, colored, stable
        float staticBonus = obj.Speed < 0.05f ? 0.3f : 0f;
        var sig = obj.GetSignature();
        float colorBonus = sig.Saturation > 0.3f ? 0.3f : 0f;
        float stableBonus = sig.Stability > 0.6f ? 0.2f : 0f;

        return Math.Min(1f, staticBonus + colorBonus + stableBonus);
    }
}
