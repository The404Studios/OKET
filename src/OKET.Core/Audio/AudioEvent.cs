namespace OKET.Core.Audio;

/// <summary>
/// Types of audio events the system can recognize.
/// </summary>
public enum AudioEventType
{
    Unknown = 0,

    // Combat sounds
    GunfireNear,
    GunfireFar,
    HitMarker,          // Confirmation of hit
    BulletImpact,
    Explosion,

    // Zombie sounds
    ZombieGroan,
    ZombieScream,
    ZombieFootsteps,
    HeadcrabSqueak,

    // Player sounds
    DamageTaken,        // Player hurt sound
    Heartbeat,          // Low health indicator
    DeathSound,
    Footsteps,

    // Weapon sounds
    ReloadStart,
    ReloadComplete,
    EmptyClick,         // Out of ammo
    WeaponSwitch,

    // Environment sounds
    BarricadeHit,
    BarricadeRepair,
    DoorOpen,
    AmbientAlarm,

    // UI sounds
    PointsGained,
    WaveStart,
    WaveEnd,
}

/// <summary>
/// A detected audio event with timing and confidence.
/// </summary>
public sealed class AudioEvent
{
    /// <summary>Type of audio event.</summary>
    public AudioEventType Type { get; init; }

    /// <summary>Confidence in the classification [0, 1].</summary>
    public float Confidence { get; init; }

    /// <summary>When the event occurred.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Estimated direction (-1 = left, 0 = center, 1 = right).</summary>
    public float Direction { get; init; }

    /// <summary>Relative loudness [0, 1].</summary>
    public float Intensity { get; init; }

    /// <summary>Duration of the event in milliseconds.</summary>
    public float DurationMs { get; init; }

    /// <summary>Frame ID this was associated with (for fusion).</summary>
    public long AssociatedFrameId { get; set; }

    public bool IsThreatSound => Type is
        AudioEventType.ZombieGroan or
        AudioEventType.ZombieScream or
        AudioEventType.ZombieFootsteps or
        AudioEventType.HeadcrabSqueak;

    public bool IsCombatSound => Type is
        AudioEventType.GunfireNear or
        AudioEventType.HitMarker or
        AudioEventType.Explosion;

    public bool IsPlayerDamageSound => Type is
        AudioEventType.DamageTaken or
        AudioEventType.Heartbeat;
}

/// <summary>
/// Result from audio processing for a time window.
/// </summary>
public sealed class AudioSnapshot
{
    /// <summary>Timestamp of this snapshot.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>All events detected in this window.</summary>
    public IReadOnlyList<AudioEvent> Events { get; init; } = [];

    /// <summary>Overall audio level (RMS).</summary>
    public float AverageLevel { get; init; }

    /// <summary>Peak audio level in window.</summary>
    public float PeakLevel { get; init; }

    /// <summary>Spectral centroid (brightness indicator).</summary>
    public float SpectralCentroid { get; init; }

    /// <summary>Whether audio capture is healthy.</summary>
    public bool IsValid { get; init; }

    public bool HasThreatSounds => Events.Any(e => e.IsThreatSound);
    public bool HasCombatSounds => Events.Any(e => e.IsCombatSound);
    public bool HasDamageSounds => Events.Any(e => e.IsPlayerDamageSound);
}
