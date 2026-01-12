namespace OKET.Core.Cognition;

/// <summary>
/// Fused belief state from all sensory modalities.
/// This is the "single truth" the policy operates on.
/// </summary>
public sealed record BeliefState
{
    /// <summary>Frame ID this belief corresponds to.</summary>
    public long FrameId { get; init; }

    /// <summary>Timestamp of belief formation.</summary>
    public DateTime Timestamp { get; init; }

    // --- Core beliefs ---

    /// <summary>Overall threat level [0, 1]. 0 = safe, 1 = extreme danger.</summary>
    public float ThreatLevel { get; init; }

    /// <summary>Primary threat direction (-1 = left, 0 = center, 1 = right).</summary>
    public float ThreatDirection { get; init; }

    /// <summary>Estimated distance to nearest threat (normalized 0-1, lower = closer).</summary>
    public float ThreatProximity { get; init; }

    /// <summary>Whether a hit was just confirmed (from audio + visual).</summary>
    public bool HitConfirmed { get; init; }

    /// <summary>Confidence in hit confirmation [0, 1].</summary>
    public float HitConfidence { get; init; }

    /// <summary>Current reload state estimate.</summary>
    public ReloadBelief ReloadState { get; init; } = ReloadBelief.Ready;

    /// <summary>Confidence in reload state [0, 1].</summary>
    public float ReloadConfidence { get; init; }

    /// <summary>Health risk level [0, 1]. Higher = more danger.</summary>
    public float HealthRisk { get; init; }

    /// <summary>Whether barricade interaction is occurring.</summary>
    public bool RepairActive { get; init; }

    /// <summary>Confidence in repair state [0, 1].</summary>
    public float RepairConfidence { get; init; }

    // --- Meta-beliefs (about belief quality) ---

    /// <summary>Overall confidence in this belief state [0, 1].</summary>
    public float Confidence { get; init; }

    /// <summary>Agreement between vision and audio [0, 1].</summary>
    public float SensoryAgreement { get; init; }

    /// <summary>How much this belief changed from previous [0, 1].</summary>
    public float BeliefDelta { get; init; }

    // --- Source contributions ---

    /// <summary>How much vision contributed to this belief [0, 1].</summary>
    public float VisionContribution { get; init; }

    /// <summary>How much audio contributed to this belief [0, 1].</summary>
    public float AudioContribution { get; init; }

    /// <summary>How much HUD contributed to this belief [0, 1].</summary>
    public float HudContribution { get; init; }

    // --- Derived states ---

    /// <summary>Conflict between audio and visual modalities [0, 1]. Inverse of SensoryAgreement.</summary>
    public float AudioVisualConflict => 1f - SensoryAgreement;

    public bool IsUnderAttack => ThreatLevel > 0.5f && ThreatProximity > 0.5f;
    public bool IsSafe => ThreatLevel < 0.2f;
    public bool NeedsReload => ReloadState == ReloadBelief.Empty && ReloadConfidence > 0.5f;
    public bool IsReloading => ReloadState == ReloadBelief.Reloading && ReloadConfidence > 0.5f;
    public bool IsCritical => HealthRisk > 0.7f;

    /// <summary>
    /// Convert to feature vector for ML models.
    /// </summary>
    public float[] ToFeatureVector()
    {
        return new[]
        {
            ThreatLevel,
            ThreatDirection,
            ThreatProximity,
            HitConfirmed ? 1f : 0f,
            HitConfidence,
            (float)ReloadState / 3f,
            ReloadConfidence,
            HealthRisk,
            RepairActive ? 1f : 0f,
            Confidence,
            SensoryAgreement,
            BeliefDelta
        };
    }

    public const int FeatureVectorSize = 12;
}

public enum ReloadBelief
{
    Ready = 0,      // Magazine has ammo
    Low = 1,        // Low ammo
    Empty = 2,      // Need reload
    Reloading = 3   // Currently reloading
}
