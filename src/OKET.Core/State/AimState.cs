using OKET.Core.Types;

namespace OKET.Core.State;

/// <summary>
/// Current aiming state relative to a target.
/// </summary>
public sealed class AimState
{
    /// <summary>Screen-space crosshair position.</summary>
    public Vector2 CrosshairPosition { get; init; }

    /// <summary>Current target (if any).</summary>
    public OKET.Core.Detection.Detection? Target { get; init; }

    /// <summary>Screen-space offset from crosshair to target aim point.</summary>
    public Vector2 OffsetToTarget { get; init; }

    /// <summary>Distance to target in pixels.</summary>
    public float PixelDistance => OffsetToTarget.Length;

    /// <summary>Whether crosshair is on target (within tolerance).</summary>
    public bool IsOnTarget { get; init; }

    /// <summary>How long we've been tracking this target (ms).</summary>
    public float TrackingDuration { get; init; }

    /// <summary>Quality of tracking [0, 1]. Higher = more stable tracking.</summary>
    public float TrackingQuality { get; init; }

    /// <summary>Recent hit marker detected.</summary>
    public bool HitConfirmed { get; init; }

    /// <summary>Tolerance radius for "on target" (pixels).</summary>
    public const float OnTargetTolerance = 30f;

    public static AimState NoTarget(Vector2 crosshair) => new()
    {
        CrosshairPosition = crosshair,
        Target = null,
        OffsetToTarget = Vector2.Zero,
        IsOnTarget = false,
        TrackingDuration = 0,
        TrackingQuality = 0,
        HitConfirmed = false
    };
}
