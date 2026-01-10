using OKET.Core.Types;
using OKET.Core.Detection;

namespace OKET.Core.State;

/// <summary>
/// Complete game state at a point in time.
/// This is the primary input to the decision layer.
/// </summary>
public sealed class GameState
{
    /// <summary>Frame ID this state was built from.</summary>
    public long FrameId { get; init; }

    /// <summary>Timestamp when state was captured.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>HUD-derived player state.</summary>
    public HudState Hud { get; init; } = new();

    /// <summary>Current aiming state.</summary>
    public AimState Aim { get; init; } = AimState.NoTarget(new Vector2(960, 540));

    /// <summary>All current detections.</summary>
    public DetectionResult Detections { get; init; } = new() { Detections = [] };

    /// <summary>Screen resolution.</summary>
    public Vector2 ScreenSize { get; init; } = new(1920, 1080);

    /// <summary>Estimated nearest threat distance (pixels from center).</summary>
    public float NearestThreatDistance { get; init; } = float.MaxValue;

    /// <summary>Number of threats in field of view.</summary>
    public int ThreatsInFov => Detections.ThreatCount;

    /// <summary>Whether we appear to be stuck (no position change despite movement).</summary>
    public bool IsStuck { get; init; }

    /// <summary>Frames since last confirmed hit.</summary>
    public int FramesSinceHit { get; init; }

    /// <summary>Frames since taking damage.</summary>
    public int FramesSinceDamage { get; init; }

    /// <summary>Current "danger level" [0, 1].</summary>
    public float DangerLevel =>
        Math.Clamp(
            (ThreatsInFov * 0.1f) +
            (Hud.IsLowHealth ? 0.3f : 0) +
            (Hud.IsCriticalHealth ? 0.3f : 0) +
            (NearestThreatDistance < 200 ? 0.3f : 0),
            0f, 1f);

    /// <summary>
    /// Convert to a numeric feature vector for ML models.
    /// </summary>
    public float[] ToFeatureVector()
    {
        return
        [
            // Self state (normalized)
            Hud.Health / 100f,
            Hud.Armor / 100f,
            Hud.AmmoClip / 30f,  // Assuming 30-round mag as baseline
            Hud.AmmoReserve / 200f,
            Hud.IsReloading ? 1f : 0f,

            // Threat assessment
            Math.Min(ThreatsInFov, 10) / 10f,
            Math.Clamp(1f - NearestThreatDistance / 1000f, 0f, 1f),
            DangerLevel,

            // Aim state
            Aim.Target != null ? 1f : 0f,
            Aim.IsOnTarget ? 1f : 0f,
            Math.Clamp(Aim.PixelDistance / 500f, 0f, 1f),
            Aim.Target?.Confidence ?? 0f,

            // Aim offset (normalized)
            Aim.OffsetToTarget.X / ScreenSize.X,
            Aim.OffsetToTarget.Y / ScreenSize.Y,

            // Context
            IsStuck ? 1f : 0f,
            Math.Min(FramesSinceHit, 300) / 300f,
            Math.Min(FramesSinceDamage, 300) / 300f,

            // Wave info
            Hud.Wave / 20f,
        ];
    }

    public const int FeatureVectorSize = 18;
}
