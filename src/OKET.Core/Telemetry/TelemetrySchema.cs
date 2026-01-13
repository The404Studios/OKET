namespace OKET.Core.Telemetry;

/// <summary>
/// Telemetry schema version - all tokens must include this.
/// Breaking changes require version bump.
/// </summary>
public static class TelemetrySchema
{
    public const int VersionMajor = 1;
    public const int VersionMinor = 0;
    public const string Version = "1.0";

    /// <summary>Maximum entities per tick (hard cap).</summary>
    public const int MaxEntitiesPerTick = 64;

    /// <summary>Maximum UI texts per tick (hard cap).</summary>
    public const int MaxUiTextsPerTick = 16;

    /// <summary>Maximum audio cues per tick.</summary>
    public const int MaxAudioCuesPerTick = 8;

    /// <summary>Maximum predictions per tick.</summary>
    public const int MaxPredictionsPerTick = 32;
}

/// <summary>
/// Token types - stable, bounded set.
/// Adding new types requires schema version bump.
/// </summary>
public enum TokenType : byte
{
    /// <summary>Player's own state (health, ammo, etc).</summary>
    SelfState = 1,

    /// <summary>Detected entity (enemy, item, ally).</summary>
    Entity = 2,

    /// <summary>UI/OCR text recognition.</summary>
    UiText = 3,

    /// <summary>Audio cue detection.</summary>
    AudioCue = 4,

    /// <summary>Navigation waypoint/state.</summary>
    Navigation = 5,

    /// <summary>Action taken by agent.</summary>
    Action = 6,

    /// <summary>Outcome/reward signal.</summary>
    Outcome = 7,

    /// <summary>Prediction made by agent.</summary>
    Prediction = 8,

    /// <summary>Prediction error measurement.</summary>
    Error = 9
}

/// <summary>
/// Header for all telemetry tokens.
/// Fixed format for replay compatibility.
/// </summary>
public readonly record struct TokenHeader(
    int VersionMajor,
    int VersionMinor,
    long TickId,
    long TimestampUnixMs,
    TokenType Type,
    float Confidence
)
{
    public static TokenHeader Create(long tickId, TokenType type, float confidence = 1f) => new(
        TelemetrySchema.VersionMajor,
        TelemetrySchema.VersionMinor,
        tickId,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        type,
        confidence
    );
}

/// <summary>
/// Marker interface for token payloads.
/// All payloads must be small and bounded.
/// </summary>
public interface ITokenPayload { }

/// <summary>
/// Complete telemetry token with header and payload.
/// </summary>
public readonly record struct TelemetryToken(TokenHeader Header, ITokenPayload Payload)
{
    public static TelemetryToken Create(long tickId, TokenType type, ITokenPayload payload, float confidence = 1f) =>
        new(TokenHeader.Create(tickId, type, confidence), payload);
}

/// <summary>
/// Entity kinds - stable enumeration.
/// </summary>
public enum EntityKind : byte
{
    Unknown = 0,
    Player = 1,
    Zombie = 2,
    Item = 3,
    Survivor = 4,
    Teammate = 5,
    Headcrab = 6,
    FastZombie = 7,
    PoisonZombie = 8,
    HealthPack = 10,
    AmmoCrate = 11,
    WeaponCrate = 12
}

/// <summary>
/// Entity token - detected object in the world.
/// Coordinates are normalized [0..1] for resolution independence.
/// </summary>
public readonly record struct EntityToken(
    EntityKind Kind,
    int TrackId,
    float X,              // normalized center X [0..1]
    float Y,              // normalized center Y [0..1]
    float W,              // normalized width [0..1]
    float H,              // normalized height [0..1]
    float DistanceM,      // estimated distance in meters, -1 if unknown
    float Vx,             // velocity X (normalized/sec), 0 if unknown
    float Vy              // velocity Y (normalized/sec), 0 if unknown
) : ITokenPayload
{
    public bool HasDistance => DistanceM >= 0;
    public bool HasVelocity => Vx != 0 || Vy != 0;
}

/// <summary>
/// UI text token - OCR recognized text.
/// </summary>
public readonly record struct UiTextToken(
    string Text,
    float X,              // normalized position [0..1]
    float Y,              // normalized position [0..1]
    float SizeHint        // font height normalized or px
) : ITokenPayload;

/// <summary>
/// Self state token - player's own state.
/// All values normalized [0..1].
/// </summary>
public readonly record struct SelfStateToken(
    float Health01,
    float Armor01,
    float Ammo01,
    bool IsReloading,
    bool IsDead,
    int WaveNumber
) : ITokenPayload;

/// <summary>
/// Audio cue types.
/// </summary>
public enum AudioCueType : byte
{
    Unknown = 0,
    Gunshot = 1,
    Footstep = 2,
    ZombieMoan = 3,
    Reload = 4,
    Hit = 5,
    Explosion = 6,
    ItemPickup = 7
}

/// <summary>
/// Audio cue token - detected sound.
/// </summary>
public readonly record struct AudioCueToken(
    AudioCueType CueType,
    float DirectionDeg,   // 0-360, 0 = forward, 90 = right
    float Volume01,       // relative volume [0..1]
    float DistanceHint    // estimated distance, -1 if unknown
) : ITokenPayload;

/// <summary>
/// Navigation token - pathfinding state.
/// </summary>
public readonly record struct NavigationToken(
    float TargetX,        // normalized target position
    float TargetY,
    float DistanceToTarget,
    bool IsBlocked,
    float PathProgress01  // 0 = just started, 1 = arrived
) : ITokenPayload;

/// <summary>
/// Action token - action taken by agent.
/// </summary>
public readonly record struct ActionToken(
    byte ActionType,      // maps to ActionType enum
    float ParamA,
    float ParamB,
    float Confidence
) : ITokenPayload;

/// <summary>
/// Outcome token - result of an action.
/// </summary>
public readonly record struct OutcomeToken(
    byte ActionType,
    bool Success,
    float Reward,
    string? FailureReason
) : ITokenPayload;
