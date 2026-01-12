namespace OKET.Core.Trust;

/// <summary>
/// Token Authorization Chain - The Certificate Chain Validator.
///
/// A token is NOT authorized just because it matches a prototype.
/// It must satisfy the FULL CHAIN:
///
///   Root Invariant
///        ↓
///   Validated Gradient Object (from Stabilizer)
///        ↓
///   Prototype Match (from Vault)
///        ↓
///   Context Check
///        ↓
///   Chain Consistency (temporal)
///        ↓
///   AUTHORIZED TOKEN
///
/// If ANY link breaks → NO authority.
/// This is literally certificate validation logic.
///
/// Final authorization score:
/// S_auth = S_root^w_r * S_match^w_m * S_ctx^w_c * S_chain^w_t * Trust(p)^w_p
///
/// Authorized token if S_auth >= τ_token (with hysteresis)
/// </summary>
public sealed class TokenAuthorizationChain
{
    private readonly GradientStabilizer _stabilizer;
    private readonly PrototypeVault _vault;

    // Authorization state per token (for hysteresis)
    private readonly Dictionary<int, TokenAuthState> _authStates = new();

    // Statistics
    private int _totalValidations;
    private int _authorized;
    private int _rootFailed;
    private int _matchFailed;
    private int _contextFailed;
    private int _chainFailed;

    public GradientStabilizer Stabilizer => _stabilizer;
    public PrototypeVault Vault => _vault;
    public int TotalValidations => _totalValidations;
    public int AuthorizedCount => _authorized;
    public float AuthorizationRate => _totalValidations > 0
        ? (float)_authorized / _totalValidations
        : 0;

    public TokenAuthorizationChain()
    {
        _stabilizer = new GradientStabilizer();
        _vault = new PrototypeVault();
    }

    /// <summary>
    /// Validate and authorize a token through the full chain.
    /// </summary>
    public TokenAuthorizationResult Authorize(TokenAuthorizationInput input, long frameId)
    {
        _totalValidations++;

        // Get or create auth state for hysteresis
        if (!_authStates.TryGetValue(input.TokenId, out var authState))
        {
            authState = new TokenAuthState(input.TokenId);
            _authStates[input.TokenId] = authState;
        }

        // === LINK 1: ROOT INVARIANT VALIDATION ===
        var stabInput = CreateStabilizationInput(input);
        var stabResult = _stabilizer.Stabilize(stabInput, frameId);

        if (!stabResult.IsStabilized)
        {
            _rootFailed++;
            authState.RecordRejection(ChainLink.Root);
            return TokenAuthorizationResult.Unauthorized(
                $"Root invariant failed: {stabResult.RejectReason}",
                ChainLink.Root);
        }

        float rootScore = stabResult.RootScore;

        // === LINK 2: PROTOTYPE MATCH ===
        var signature = CreateSignatureVector(input);
        var matchResult = _vault.Match(signature);

        float matchScore;
        float prototypeTrust;
        int? protoId = null;

        if (matchResult.Found)
        {
            matchScore = matchResult.MatchScore;
            prototypeTrust = matchResult.PrototypeTrust;
            protoId = matchResult.Prototype!.Id;
            matchResult.Prototype.MarkObserved(frameId);
        }
        else
        {
            // Novel object - can still be authorized but lower confidence
            matchScore = input.NoveltyScore > 0.5f ? 0.4f : 0.2f;
            prototypeTrust = 0.3f;
        }

        if (matchScore < RootInvariants.MinMatchScore && !IsProbeAllowed(input))
        {
            _matchFailed++;
            authState.RecordRejection(ChainLink.Match);
            return TokenAuthorizationResult.Unauthorized(
                $"Match score {matchScore:F2} < {RootInvariants.MinMatchScore}",
                ChainLink.Match);
        }

        // === LINK 3: CONTEXT CHECK ===
        float contextScore = ComputeContextScore(input, matchResult.Prototype);

        if (contextScore < RootInvariants.MinContextScore)
        {
            _contextFailed++;
            authState.RecordRejection(ChainLink.Context);
            return TokenAuthorizationResult.Unauthorized(
                $"Context score {contextScore:F2} < {RootInvariants.MinContextScore}",
                ChainLink.Context);
        }

        // === LINK 4: CHAIN CONSISTENCY (temporal) ===
        float chainScore = ComputeChainScore(input, authState, frameId);

        if (chainScore < 0.5f && authState.TotalFrames >= 6)
        {
            _chainFailed++;
            authState.RecordRejection(ChainLink.Chain);
            return TokenAuthorizationResult.Unauthorized(
                $"Chain score {chainScore:F2} < 0.5 (flickering)",
                ChainLink.Chain);
        }

        // === COMPUTE FINAL AUTHORIZATION SCORE ===
        float authScore = RootInvariants.ComputeAuthorizationScore(
            rootScore,
            matchScore,
            contextScore,
            chainScore,
            prototypeTrust);

        // === HYSTERESIS CHECK ===
        var newState = RootInvariants.CheckAuthorization(
            authScore,
            authState.CurrentState);

        authState.UpdateState(newState, authScore, frameId);

        if (newState != AuthorizationState.Authorized)
        {
            return TokenAuthorizationResult.Unauthorized(
                $"Authorization score {authScore:F2} below threshold",
                ChainLink.Final);
        }

        // === AUTHORIZED ===
        _authorized++;

        return TokenAuthorizationResult.Authorized(
            authScore,
            new ChainScores
            {
                Root = rootScore,
                Match = matchScore,
                Context = contextScore,
                Chain = chainScore,
                PrototypeTrust = prototypeTrust
            },
            protoId,
            matchResult.Prototype?.Name);
    }

    /// <summary>
    /// Check if probe action is allowed (for novel patterns).
    /// </summary>
    private static bool IsProbeAllowed(TokenAuthorizationInput input)
    {
        // Allow probe if:
        // - Low risk situation
        // - High info gain potential
        // - Not in immediate danger
        return input.ThreatLevel < 0.3f &&
               input.NoveltyScore > 0.5f &&
               input.Health > 0.4f;
    }

    /// <summary>
    /// Compute context score.
    /// </summary>
    private static float ComputeContextScore(
        TokenAuthorizationInput input,
        VaultPrototype? prototype)
    {
        // HUD overlap penalty
        if (input.HudOverlap > RootInvariants.MaxHudOverlap)
            return 0;

        // If we have a prototype, use its context profile
        if (prototype != null)
        {
            return prototype.Context.ComputeScore(input.CenterX, input.CenterY, input.RoiId);
        }

        // For novel objects, context is based on screen position
        // Things in center are more likely valid
        float centerDist = MathF.Sqrt(
            (input.CenterX - 0.5f) * (input.CenterX - 0.5f) +
            (input.CenterY - 0.5f) * (input.CenterY - 0.5f));

        return Math.Clamp(1f - centerDist, 0.3f, 1f);
    }

    /// <summary>
    /// Compute chain consistency score (temporal).
    /// </summary>
    private static float ComputeChainScore(
        TokenAuthorizationInput input,
        TokenAuthState authState,
        long frameId)
    {
        // Record this frame's signature
        authState.RecordSignature(input.SignatureDrift, frameId);

        // Check majority vote (4 of 6 frames)
        int validFrames = authState.GetValidFrameCount(6);
        float voteFactor = validFrames / 6f;

        // Check signature drift
        float medianDrift = authState.GetMedianDrift(6);
        float driftFactor = MathF.Exp(-5f * medianDrift); // Penalize drift

        // Age factor (new objects get lower chain score)
        float ageFactor = Math.Min(1f, authState.TotalFrames / 6f);

        return voteFactor * 0.4f + driftFactor * 0.4f + ageFactor * 0.2f;
    }

    /// <summary>
    /// Create stabilization input from authorization input.
    /// </summary>
    private static StabilizationInput CreateStabilizationInput(TokenAuthorizationInput input)
    {
        return new StabilizationInput
        {
            ObjectId = input.TokenId,
            SegmentationQuality = input.SegmentationQuality,
            AreaNorm = input.AreaNorm,
            SignalToNoise = input.SignalToNoise,
            MotionCoherence = input.MotionCoherence,
            EdgeDensity = input.EdgeDensity,
            TemporalStability = input.TemporalStability,
            IsMoving = input.Speed > 0.05f,
            IsFlashEvent = input.IsFlashEvent,
            ColorAgreement = input.ColorAgreement,
            Saturation = input.Saturation,
            Value = input.Value,
            SignatureDrift = input.SignatureDrift,
            HudOverlap = input.HudOverlap,
            IsStatic = input.Speed < 0.02f,
            CenterX = input.CenterX,
            CenterY = input.CenterY,
            CameraMotionBias = input.CameraMotionBias,
            Speed = input.Speed,
            Jitter = input.Jitter
        };
    }

    /// <summary>
    /// Create signature vector from input.
    /// </summary>
    private static SignatureVector CreateSignatureVector(TokenAuthorizationInput input)
    {
        var sig = new SignatureVector();
        sig.FillFromGradient(new GradientSignatureInputs
        {
            MeanVx = input.VelocityX,
            MeanVy = input.VelocityY,
            Speed = input.Speed,
            Acceleration = input.Acceleration,
            MotionCoherence = input.MotionCoherence,
            Jitter = input.Jitter,
            AreaNorm = input.AreaNorm,
            AspectRatio = input.AspectRatio,
            Compactness = input.Compactness,
            EdgeDensity = input.EdgeDensity,
            Hu1 = 0, Hu2 = 0, Hu3 = 0, // Optional
            ContourComplexity = 0,
            VerticalityBias = input.AspectRatio > 1 ? 1 : 0,
            HueMean = input.HueMean,
            HueVar = input.HueVar,
            SatMean = input.Saturation,
            SatVar = 0,
            ValMean = input.Value,
            ValVar = 0,
            HueHist = new float[6],
            Persistence = input.Persistence,
            TemporalStability = input.TemporalStability,
            OcclusionRate = 0,
            SignatureDrift = input.SignatureDrift,
            ReappearanceRate = 0,
            FrameConsistency = 0,
            LifetimeConfidence = input.Confidence,
            Cx = input.CenterX,
            Cy = input.CenterY,
            RoiId = input.RoiId,
            DepthHint = 0,
            ScreenVelocity = input.Speed,
            CameraMotionBias = input.CameraMotionBias,
            HudOverlap = input.HudOverlap,
            EdgeProximity = 0,
            SegmentationQuality = input.SegmentationQuality,
            SignalToNoise = input.SignalToNoise,
            Contrast = input.Contrast,
            LightingStability = 1,
            NoveltyScore = input.NoveltyScore,
            ProtoConfidence = input.Confidence
        });
        return sig;
    }

    /// <summary>
    /// Record outcome for a token.
    /// </summary>
    public void RecordOutcome(int tokenId, ActionId action, bool success, float reward, float risk)
    {
        if (_authStates.TryGetValue(tokenId, out var authState))
        {
            authState.RecordOutcome(success);

            // Update prototype if matched
            if (authState.MatchedPrototypeId.HasValue)
            {
                _vault.RecordActionOutcome(
                    authState.MatchedPrototypeId.Value,
                    action,
                    new ActionOutcome
                    {
                        Success = success,
                        Reward = reward,
                        Risk = risk,
                        InfoGain = success ? 0.1f : 0.3f // Learn more from failures
                    });
            }
        }
    }

    /// <summary>
    /// Commit a novel token to the vault as a new prototype.
    /// Only call for stabilized, consistent tokens.
    /// </summary>
    public int? CommitNovelToken(int tokenId, TokenAuthorizationInput input)
    {
        if (!_authStates.TryGetValue(tokenId, out var authState))
            return null;

        if (!authState.IsFullyStabilized)
            return null;

        var signature = CreateSignatureVector(input);
        var context = new ContextProfile
        {
            CxMean = input.CenterX,
            CyMean = input.CenterY,
            CxVar = 0.1f,
            CyVar = 0.1f,
            RoiMask = 1 << input.RoiId
        };

        var proto = _vault.Commit(signature, context);
        authState.MatchedPrototypeId = proto.Id;

        return proto.Id;
    }

    /// <summary>
    /// Cleanup old auth states.
    /// </summary>
    public void Cleanup(long currentFrame, int maxAge = 300)
    {
        var toRemove = _authStates
            .Where(kv => currentFrame - kv.Value.LastFrameId > maxAge)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in toRemove)
            _authStates.Remove(id);

        _stabilizer.Cleanup(currentFrame, maxAge);
    }

    /// <summary>
    /// Get diagnostics.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"""
            === TOKEN AUTHORIZATION CHAIN ===
            Validations: {_totalValidations}
            Authorized: {_authorized} ({AuthorizationRate:P1})
            Failures: root={_rootFailed}, match={_matchFailed}, ctx={_contextFailed}, chain={_chainFailed}
            Active States: {_authStates.Count}

            {_stabilizer.GetDiagnostics()}
            {_vault.GetDiagnostics()}
            =================================
            """;
    }
}

/// <summary>
/// Input for token authorization.
/// </summary>
public readonly struct TokenAuthorizationInput
{
    public int TokenId { get; init; }

    // Root invariant inputs
    public float SegmentationQuality { get; init; }
    public float AreaNorm { get; init; }
    public float SignalToNoise { get; init; }
    public float MotionCoherence { get; init; }
    public float EdgeDensity { get; init; }
    public float TemporalStability { get; init; }
    public float Speed { get; init; }
    public float Jitter { get; init; }
    public float Persistence { get; init; }
    public bool IsFlashEvent { get; init; }

    // Signature inputs
    public float VelocityX { get; init; }
    public float VelocityY { get; init; }
    public float Acceleration { get; init; }
    public float AspectRatio { get; init; }
    public float Compactness { get; init; }
    public float HueMean { get; init; }
    public float HueVar { get; init; }
    public float Saturation { get; init; }
    public float Value { get; init; }
    public float Confidence { get; init; }

    // Context inputs
    public float CenterX { get; init; }
    public float CenterY { get; init; }
    public int RoiId { get; init; }
    public float HudOverlap { get; init; }
    public float CameraMotionBias { get; init; }

    // Chain inputs
    public float SignatureDrift { get; init; }
    public float ColorAgreement { get; init; }
    public float Contrast { get; init; }

    // Meta
    public float NoveltyScore { get; init; }
    public float ThreatLevel { get; init; }
    public float Health { get; init; }
}

/// <summary>
/// Result of token authorization.
/// </summary>
public readonly struct TokenAuthorizationResult
{
    public bool IsAuthorized { get; init; }
    public float AuthorizationScore { get; init; }
    public ChainScores Scores { get; init; }
    public int? MatchedPrototypeId { get; init; }
    public string? PrototypeName { get; init; }
    public string? RejectReason { get; init; }
    public ChainLink FailedAt { get; init; }

    public static TokenAuthorizationResult Unauthorized(string reason, ChainLink failedAt) =>
        new()
        {
            IsAuthorized = false,
            RejectReason = reason,
            FailedAt = failedAt
        };

    public static TokenAuthorizationResult Authorized(
        float score,
        ChainScores scores,
        int? protoId,
        string? protoName) =>
        new()
        {
            IsAuthorized = true,
            AuthorizationScore = score,
            Scores = scores,
            MatchedPrototypeId = protoId,
            PrototypeName = protoName
        };
}

/// <summary>
/// Scores at each chain link.
/// </summary>
public readonly struct ChainScores
{
    public float Root { get; init; }
    public float Match { get; init; }
    public float Context { get; init; }
    public float Chain { get; init; }
    public float PrototypeTrust { get; init; }
}

/// <summary>
/// Chain link identifier.
/// </summary>
public enum ChainLink
{
    None,
    Root,
    Match,
    Context,
    Chain,
    Final
}

/// <summary>
/// Authorization state for a token (for hysteresis).
/// </summary>
internal sealed class TokenAuthState
{
    private readonly int _tokenId;
    private readonly Queue<(long frame, bool valid, float drift)> _frameHistory = new();
    private const int MaxHistory = 30;

    private AuthorizationState _currentState = AuthorizationState.Unknown;
    private float _lastAuthScore;
    private long _lastFrameId;
    private int _consecutiveSuccesses;
    private int _totalFrames;
    private int _consecutiveRejections;

    public int TokenId => _tokenId;
    public AuthorizationState CurrentState => _currentState;
    public long LastFrameId => _lastFrameId;
    public int TotalFrames => _totalFrames;
    public int? MatchedPrototypeId { get; set; }
    public bool IsFullyStabilized => _consecutiveSuccesses >= 10;

    public TokenAuthState(int tokenId)
    {
        _tokenId = tokenId;
    }

    public void UpdateState(AuthorizationState newState, float authScore, long frameId)
    {
        _currentState = newState;
        _lastAuthScore = authScore;
        _lastFrameId = frameId;
        _totalFrames++;

        if (newState == AuthorizationState.Authorized)
        {
            _consecutiveSuccesses++;
            _consecutiveRejections = 0;
        }
        else
        {
            _consecutiveRejections++;
        }
    }

    public void RecordRejection(ChainLink link)
    {
        _consecutiveSuccesses = 0;
        _consecutiveRejections++;
    }

    public void RecordSignature(float drift, long frameId)
    {
        _frameHistory.Enqueue((frameId, _currentState == AuthorizationState.Authorized, drift));
        while (_frameHistory.Count > MaxHistory)
            _frameHistory.Dequeue();
    }

    public void RecordOutcome(bool success)
    {
        if (success)
            _consecutiveSuccesses++;
    }

    public int GetValidFrameCount(int lastN)
    {
        return _frameHistory.TakeLast(lastN).Count(f => f.valid);
    }

    public float GetMedianDrift(int lastN)
    {
        var drifts = _frameHistory.TakeLast(lastN).Select(f => f.drift).OrderBy(x => x).ToList();
        if (drifts.Count == 0) return 0;
        return drifts[drifts.Count / 2];
    }
}
