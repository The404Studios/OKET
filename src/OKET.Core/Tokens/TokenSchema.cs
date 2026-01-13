using OKET.Core.Types;

namespace OKET.Core.Tokens;

/// <summary>
/// LOCK 1: Canonical Token Schema Contract v1.0
///
/// This is the ONE format for all perception tokens.
/// Tokens are LOSSY on purpose - only meaningful state changes emit tokens.
///
/// Rules:
/// 1. All tokens must fit this schema
/// 2. Schema is versioned - breaking changes require version bump
/// 3. Server records but NEVER decides
/// 4. Tokens only emit when: affects decisions, changes state, crosses threshold
/// </summary>
public static class TokenSchemaVersion
{
    public const int Major = 1;
    public const int Minor = 0;
    public const string Version = "1.0";
}

/// <summary>
/// The ONE canonical token type.
/// All perception must flow through this format.
/// </summary>
public readonly struct PerceptionToken
{
    /// <summary>Schema version for replay compatibility.</summary>
    public string SchemaVersion { get; init; }

    /// <summary>Token type - what kind of perception this represents.</summary>
    public TokenType Type { get; init; }

    /// <summary>Symbolic value label (e.g., "zombie_close", "health_critical").</summary>
    public string Value { get; init; }

    /// <summary>Confidence in this perception [0, 1]. Policy must gate on this.</summary>
    public float Confidence { get; init; }

    /// <summary>Distance in meters (null if not applicable).</summary>
    public float? Distance { get; init; }

    /// <summary>Source of distance estimate for fusion decisions.</summary>
    public DistanceSource? DistanceSource { get; init; }

    /// <summary>Velocity vector (null if not applicable or not tracked).</summary>
    public Vector2? Velocity { get; init; }

    /// <summary>Screen position if relevant.</summary>
    public Vector2? ScreenPosition { get; init; }

    /// <summary>When this perception occurred.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Frame ID for replay synchronization.</summary>
    public long FrameId { get; init; }

    /// <summary>Urgency level [0, 10]. Higher = more immediate attention needed.</summary>
    public byte Urgency { get; init; }

    /// <summary>
    /// Whether this token represents a meaningful state change.
    /// Tokens with IsSignificant=false should be filtered in most contexts.
    /// </summary>
    public bool IsSignificant { get; init; }

    public override string ToString() =>
        $"[{Type}] {Value} (conf={Confidence:P0}, dist={Distance?.ToString("F1") ?? "?"})";
}

/// <summary>
/// Strictly bounded token types.
/// Adding new types requires schema version bump.
/// </summary>
public enum TokenType : byte
{
    /// <summary>Enemy/threat perception (zombies, hostile entities).</summary>
    Enemy = 1,

    /// <summary>Item perception (ammo, health, weapons).</summary>
    Item = 2,

    /// <summary>Audio cue perception (gunshot, footstep, zombie sound).</summary>
    AudioCue = 3,

    /// <summary>UI/text perception from OCR.</summary>
    UI = 4,

    /// <summary>Navigation state (path, obstacle, goal).</summary>
    Navigation = 5,

    /// <summary>Self state (health, ammo, position).</summary>
    SelfState = 6,

    /// <summary>Teammate/ally perception.</summary>
    Ally = 7,

    /// <summary>Environment/context (zone type, danger level).</summary>
    Environment = 8
}

/// <summary>
/// Source of distance estimation - for fusion and confidence decisions.
/// </summary>
public enum DistanceSource : byte
{
    /// <summary>From bounding box size.</summary>
    BoundingBox = 1,

    /// <summary>From OCR text height.</summary>
    OCR = 2,

    /// <summary>Fused from multiple sources.</summary>
    Fused = 3,

    /// <summary>From game UI (if available).</summary>
    GameUI = 4,

    /// <summary>Unknown/estimated.</summary>
    Unknown = 0
}

/// <summary>
/// Fused distance estimate with multi-source confidence.
/// Never let distance drive actions without this confidence gating.
/// </summary>
public readonly struct FusedDistance
{
    /// <summary>Best estimate distance in meters.</summary>
    public float Distance { get; init; }

    /// <summary>Confidence in estimate [0, 1].</summary>
    public float Confidence { get; init; }

    /// <summary>Primary source of estimate.</summary>
    public DistanceSource PrimarySource { get; init; }

    /// <summary>Whether multiple sources agreed.</summary>
    public bool IsCorroborated { get; init; }

    /// <summary>Disagreement between sources (0 = perfect agreement).</summary>
    public float SourceDisagreement { get; init; }

    /// <summary>
    /// Whether this distance is trustworthy enough for action.
    /// Policy should check this before using distance.
    /// </summary>
    public bool IsTrustworthy => Confidence > 0.5f && SourceDisagreement < 0.3f;

    public static FusedDistance FromSingleSource(float distance, float confidence, DistanceSource source)
    {
        return new FusedDistance
        {
            Distance = distance,
            Confidence = confidence * 0.7f, // Single source penalty
            PrimarySource = source,
            IsCorroborated = false,
            SourceDisagreement = 0f
        };
    }

    public static FusedDistance Fuse(float bboxDistance, float bboxConf, float? ocrDistance, float? ocrConf)
    {
        if (!ocrDistance.HasValue || !ocrConf.HasValue)
        {
            return FromSingleSource(bboxDistance, bboxConf, DistanceSource.BoundingBox);
        }

        // Calculate disagreement
        float disagreement = Math.Abs(bboxDistance - ocrDistance.Value) /
            Math.Max(bboxDistance, ocrDistance.Value);

        // Weighted average based on confidence
        float totalConf = bboxConf + ocrConf.Value;
        float fusedDist = (bboxDistance * bboxConf + ocrDistance.Value * ocrConf.Value) / totalConf;

        // Fusion confidence: higher when sources agree
        float fusedConf = (bboxConf + ocrConf.Value) / 2f;
        if (disagreement < 0.2f)
        {
            fusedConf *= 1.2f; // Agreement bonus
        }
        else if (disagreement > 0.5f)
        {
            fusedConf *= 0.6f; // Disagreement penalty
        }

        return new FusedDistance
        {
            Distance = fusedDist,
            Confidence = Math.Clamp(fusedConf, 0f, 1f),
            PrimarySource = DistanceSource.Fused,
            IsCorroborated = disagreement < 0.3f,
            SourceDisagreement = disagreement
        };
    }
}
