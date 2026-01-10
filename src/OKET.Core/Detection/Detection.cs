using OKET.Core.Types;

namespace OKET.Core.Detection;

/// <summary>
/// A single detected object from the vision system.
/// </summary>
public sealed class Detection
{
    /// <summary>Unique ID for tracking across frames.</summary>
    public int TrackId { get; set; }

    /// <summary>Class of the detected object.</summary>
    public DetectionClass Class { get; init; }

    /// <summary>Confidence score [0, 1].</summary>
    public float Confidence { get; init; }

    /// <summary>Bounding box in screen coordinates.</summary>
    public BoundingBox Box { get; init; }

    /// <summary>Frame ID this detection came from.</summary>
    public long FrameId { get; init; }

    /// <summary>Estimated distance from player (if available).</summary>
    public float? EstimatedDistance { get; set; }

    /// <summary>Estimated velocity (pixels per second, if tracked).</summary>
    public Vector2? Velocity { get; set; }

    /// <summary>
    /// Priority score for targeting. Higher = more important.
    /// Computed based on class, distance, threat level.
    /// </summary>
    public float Priority { get; set; }

    /// <summary>
    /// Get the best aim point for this detection.
    /// </summary>
    public Vector2 GetAimPoint(bool preferHeadshot = true)
    {
        return Class switch
        {
            DetectionClass.Zombie or DetectionClass.FastZombie or DetectionClass.PoisonZombie =>
                preferHeadshot ? Box.HeadTarget : Box.BodyTarget,
            DetectionClass.ZombieHead => Box.Center,
            DetectionClass.Headcrab => Box.Center,
            _ => Box.Center
        };
    }

    public bool IsThreat => Class is
        DetectionClass.Zombie or
        DetectionClass.ZombieHead or
        DetectionClass.FastZombie or
        DetectionClass.PoisonZombie or
        DetectionClass.Headcrab;

    public bool IsInteractable => Class is
        DetectionClass.Barricade or
        DetectionClass.BarricadeBoard or
        DetectionClass.AmmoCrate or
        DetectionClass.WeaponCrate or
        DetectionClass.HealthKit or
        DetectionClass.Door;
}
