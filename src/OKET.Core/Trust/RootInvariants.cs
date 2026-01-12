namespace OKET.Core.Trust;

/// <summary>
/// Root of Perception Trust (RoPT) - Perceptual Axioms.
///
/// THESE ARE NEVER LEARNED. They are hand-defined and UNCHANGEABLE.
/// Like ROM/OTP in hardware trust - they are the foundation.
///
/// If a detection violates these → INVALID. No exceptions.
///
/// Axioms:
/// 1. Coherence over time exists
/// 2. Motion continuity exists
/// 3. Occlusion behaves predictably
/// 4. Physical space is locally continuous
/// 5. Cause precedes effect
///
/// These are perceptual axioms, NOT labels.
/// </summary>
public static class RootInvariants
{
    // === HARD REJECTION THRESHOLDS (instant discard) ===
    public const float MinSegmentationQuality = 0.50f;
    public const float MinAreaNorm = 0.001f;
    public const float MinSignalToNoise = 0.35f;
    public const float MaxTeleportJump = 0.15f; // screen/sec

    // === GRADIENT OBJECT VALIDITY ===
    public const float MinMotionCoherence = 0.35f;
    public const float MinEdgeDensity = 0.25f;
    public const float MinTemporalStability = 0.40f;
    public const float MinPersistence = 0.20f; // ~5-8 frames @30fps

    // === AUTHORIZATION THRESHOLDS ===
    public const float AuthOnThreshold = 0.72f;
    public const float AuthOffThreshold = 0.55f; // Hysteresis
    public const float MinMatchScore = 0.60f;
    public const float MinContextScore = 0.55f;
    public const float MaxHudOverlap = 0.20f;
    public const float MinChainFrames = 4f; // of 6
    public const float MaxSignatureDrift = 0.15f;

    // === ACTION AUTHORIZATION ===
    public const float MinExpectedReward = 0.10f;
    public const float MaxActionRisk = 0.30f;
    public const float MinTokenConfidence = 0.75f;

    // === STABILIZATION (naming) ===
    public const int MinObservationsForNaming = 50;
    public const float MinAvgAuthScoreForNaming = 0.80f;
    public const float MinOutcomeConsistency = 0.75f;

    // === AUTHORIZATION WEIGHTS ===
    public const float WeightRoot = 1.2f;
    public const float WeightMatch = 1.0f;
    public const float WeightContext = 0.8f;
    public const float WeightChain = 1.1f;
    public const float WeightPrototype = 0.6f;

    /// <summary>
    /// Validate root invariants for a gradient object.
    /// Returns (isValid, score, reason).
    /// </summary>
    public static RootValidation ValidateRootInvariants(RootInvariantInputs inputs)
    {
        // === HARD GATES (instant discard) ===
        if (inputs.SegmentationQuality < MinSegmentationQuality)
            return RootValidation.Reject("Segmentation quality below minimum");

        if (inputs.AreaNorm < MinAreaNorm)
            return RootValidation.Reject("Area too small (noise)");

        if (inputs.SignalToNoise < MinSignalToNoise)
            return RootValidation.Reject("Signal-to-noise too low");

        if (inputs.TeleportJump > MaxTeleportJump)
            return RootValidation.Reject("Teleport detected (discontinuous motion)");

        // === SOFT GATES (contribute to score) ===

        // Axiom 1: Coherence over time
        float coherenceScore = inputs.IsMoving
            ? ClampScore((inputs.MotionCoherence - MinMotionCoherence) / (1f - MinMotionCoherence))
            : 1f; // Static objects pass

        // Axiom 2: Motion continuity
        float continuityScore = ClampScore(1f - inputs.Jitter / 0.5f);

        // Axiom 3: Temporal stability
        float stabilityScore = ClampScore(
            (inputs.TemporalStability - MinTemporalStability) / (1f - MinTemporalStability));

        // Axiom 4: Physical space continuity (edge coherence)
        float spatialScore = ClampScore(
            (inputs.EdgeDensity - MinEdgeDensity) / (1f - MinEdgeDensity));

        // Axiom 5: Persistence (cause precedes effect - things don't just appear)
        float persistenceScore = ClampScore(
            (inputs.Persistence - MinPersistence) / (1f - MinPersistence));

        // Compute final root score (multiplicative - one failure breaks chain)
        float rootScore = MathF.Pow(coherenceScore, 0.3f) *
                         MathF.Pow(continuityScore, 0.2f) *
                         MathF.Pow(stabilityScore, 0.25f) *
                         MathF.Pow(spatialScore, 0.15f) *
                         MathF.Pow(persistenceScore, 0.1f);

        // Require minimum combined score
        if (rootScore < 0.3f)
            return RootValidation.Reject($"Root invariant score too low: {rootScore:F3}");

        return RootValidation.Accept(rootScore);
    }

    /// <summary>
    /// Compute full authorization score.
    /// S_auth = S_root^w_r * S_match^w_m * S_ctx^w_c * S_chain^w_t * Trust(p)^w_p
    /// </summary>
    public static float ComputeAuthorizationScore(
        float rootScore,
        float matchScore,
        float contextScore,
        float chainScore,
        float prototypeTrust)
    {
        // Multiplicative (one broken link breaks trust)
        return MathF.Pow(rootScore, WeightRoot) *
               MathF.Pow(matchScore, WeightMatch) *
               MathF.Pow(contextScore, WeightContext) *
               MathF.Pow(chainScore, WeightChain) *
               MathF.Pow(prototypeTrust, WeightPrototype);
    }

    /// <summary>
    /// Check if authorization score passes hysteresis threshold.
    /// </summary>
    public static AuthorizationState CheckAuthorization(
        float authScore,
        AuthorizationState previousState)
    {
        return previousState switch
        {
            AuthorizationState.Unauthorized => authScore >= AuthOnThreshold
                ? AuthorizationState.Authorized
                : AuthorizationState.Unauthorized,

            AuthorizationState.Authorized => authScore < AuthOffThreshold
                ? AuthorizationState.Unauthorized
                : AuthorizationState.Authorized,

            _ => authScore >= AuthOnThreshold
                ? AuthorizationState.Authorized
                : AuthorizationState.Unauthorized
        };
    }

    private static float ClampScore(float x) => Math.Clamp(x, 0f, 1f);
}

/// <summary>
/// Inputs for root invariant validation.
/// </summary>
public readonly struct RootInvariantInputs
{
    // Hard gate inputs
    public float SegmentationQuality { get; init; }
    public float AreaNorm { get; init; }
    public float SignalToNoise { get; init; }
    public float TeleportJump { get; init; }

    // Soft gate inputs
    public bool IsMoving { get; init; }
    public float MotionCoherence { get; init; }
    public float Jitter { get; init; }
    public float TemporalStability { get; init; }
    public float EdgeDensity { get; init; }
    public float Persistence { get; init; }
}

/// <summary>
/// Result of root invariant validation.
/// </summary>
public readonly struct RootValidation
{
    public bool IsValid { get; init; }
    public float Score { get; init; }
    public string? RejectReason { get; init; }

    public static RootValidation Reject(string reason) =>
        new() { IsValid = false, Score = 0, RejectReason = reason };

    public static RootValidation Accept(float score) =>
        new() { IsValid = true, Score = score, RejectReason = null };
}

/// <summary>
/// Authorization state with hysteresis.
/// </summary>
public enum AuthorizationState
{
    Unknown,
    Unauthorized,
    Authorized
}
