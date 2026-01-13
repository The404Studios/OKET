using System.Drawing;
using OKET.Core.Types;

namespace OKET.Core.Intelligence;

/// <summary>
/// An intelligent detection - a tracked object with full context.
///
/// This is NOT just a bounding box. It's a full understanding:
/// - What it is (certified class)
/// - How confident we are
/// - How it's moving
/// - What threat/opportunity it represents
/// - Trust certification level
/// - Knowledge tags for pattern recognition
///
/// YOLO gives you boxes. This gives you understanding.
/// </summary>
public sealed class IntelligentDetection
{
    // Identity
    public int TrackId { get; private set; }
    public long FirstSeenFrame { get; private set; }
    public long LastSeenFrame { get; private set; }

    // Spatial
    public BoundingBox BoundingBox { get; private set; }
    public Vector2 Position => BoundingBox.Center;
    public float Area => BoundingBox.Area;

    // Motion (computed from tracking)
    public Vector2 Velocity { get; private set; }
    public Vector2 Acceleration { get; private set; }
    public float Speed => Velocity.Length();
    public float AngularVelocity { get; private set; }
    public Vector2 PredictedPosition => Position + Velocity;

    // Classification
    public DetectionClass Class { get; private set; }
    public string ClassName => Class.ToString();
    public float Confidence { get; private set; }

    // Authority/Trust
    public TrustLevel TrustLevel { get; private set; } = TrustLevel.Unknown;
    public string? CertifiedAs { get; private set; }
    public float TrustScore { get; private set; }

    // Behavior analysis
    public float ThreatScore { get; private set; }
    public float OpportunityScore { get; private set; }
    public float Priority { get; private set; }

    // Knowledge tags
    public List<KnowledgeTag> Tags { get; } = new();

    // Computed properties
    public bool IsThreat => ThreatScore > 0.5f || Class.IsThreat();
    public bool IsOpportunity => OpportunityScore > 0.5f || Class.IsItem();
    public bool IsMoving => Speed > 0.5f;
    public bool IsApproaching => Velocity.Y > 0.1f || Velocity.Length() > 0.5f;
    public int AgeFrames => (int)(LastSeenFrame - FirstSeenFrame);
    public bool IsStable => AgeFrames > 30 && Confidence > 0.7f;

    // Rendering hints
    public Color RenderColor { get; private set; } = Color.Gray;
    public float RenderAlpha { get; private set; } = 1f;

    private IntelligentDetection() { }

    /// <summary>
    /// Create from gradient object (internal detection).
    /// </summary>
    public static IntelligentDetection FromGradient(
        GradientObjectData obj,
        int trackId,
        long frameId)
    {
        var detection = new IntelligentDetection
        {
            TrackId = trackId,
            FirstSeenFrame = frameId,
            LastSeenFrame = frameId,
            BoundingBox = obj.BoundingBox,
            Velocity = obj.Velocity,
            Confidence = obj.Confidence,
            Class = ClassifyFromGradient(obj)
        };

        detection.ComputeScores(obj);
        detection.ComputeRenderStyle();

        return detection;
    }

    /// <summary>
    /// Create from external detection (YOLO/ONNX).
    /// </summary>
    public static IntelligentDetection FromExternal(
        Detection.Detection external,
        long frameId)
    {
        var detection = new IntelligentDetection
        {
            TrackId = external.TrackId,
            FirstSeenFrame = frameId,
            LastSeenFrame = frameId,
            BoundingBox = external.Box,
            Velocity = external.Velocity ?? Vector2.Zero,
            Confidence = external.Confidence,
            Class = external.Class,
            Priority = external.Priority
        };

        detection.ComputeScoresFromClass();
        detection.ComputeRenderStyle();

        return detection;
    }

    /// <summary>
    /// Update tracking information.
    /// </summary>
    public void UpdateTracking(
        BoundingBox newBox,
        long frameId,
        float newConfidence)
    {
        var oldCenter = BoundingBox.Center;
        var newCenter = newBox.Center;

        // Compute motion
        var oldVelocity = Velocity;
        Velocity = newCenter - oldCenter;
        Acceleration = Velocity - oldVelocity;

        // Compute angular velocity
        if (oldVelocity.Length() > 0.01f && Velocity.Length() > 0.01f)
        {
            float oldAngle = MathF.Atan2(oldVelocity.Y, oldVelocity.X);
            float newAngle = MathF.Atan2(Velocity.Y, Velocity.X);
            AngularVelocity = newAngle - oldAngle;
        }

        BoundingBox = newBox;
        LastSeenFrame = frameId;

        // Smooth confidence
        Confidence = Confidence * 0.7f + newConfidence * 0.3f;

        // Update scores
        ComputeScoresFromTracking();
        ComputeRenderStyle();
    }

    /// <summary>
    /// Apply authority certification.
    /// </summary>
    public void ApplyCertification(AuthorityCertification cert)
    {
        TrustLevel = cert.Level;
        CertifiedAs = cert.CertifiedClass;
        TrustScore = cert.TrustScore;

        // Override class if certified differently
        if (cert.Level >= TrustLevel.Certified && cert.CertifiedClass != null)
        {
            if (Enum.TryParse<DetectionClass>(cert.CertifiedClass, out var certClass))
            {
                Class = certClass;
            }
        }

        // Apply behavior modifiers from certification
        ThreatScore = cert.ThreatModifier * ThreatScore + cert.BaseThreat;
        OpportunityScore = cert.OpportunityModifier * OpportunityScore + cert.BaseOpportunity;

        // Recompute priority with certification
        Priority = ComputePriority();
        ComputeRenderStyle();
    }

    /// <summary>
    /// Add a knowledge tag.
    /// </summary>
    public void AddTag(KnowledgeTag tag)
    {
        Tags.Add(tag);
    }

    /// <summary>
    /// Get aim point (where to aim).
    /// </summary>
    public Vector2 GetAimPoint(bool preferHeadshot = false)
    {
        if (preferHeadshot)
            return BoundingBox.GetPoint(0.5f, 0.15f); // Head area

        return BoundingBox.GetPoint(0.5f, 0.4f); // Center mass
    }

    /// <summary>
    /// Predict position N frames ahead.
    /// </summary>
    public Vector2 PredictPosition(int frames)
    {
        return Position + Velocity * frames + Acceleration * 0.5f * frames * frames;
    }

    /// <summary>
    /// Compute IoU with another detection.
    /// </summary>
    public float IoU(IntelligentDetection other)
    {
        return BoundingBox.IoU(other.BoundingBox);
    }

    private static DetectionClass ClassifyFromGradient(GradientObjectData obj)
    {
        // Classify based on gradient properties
        if (obj.IsMoving && obj.AspectRatio > 0.5f && obj.AspectRatio < 2f)
        {
            // Humanoid shape + moving = likely threat
            if (obj.DominantHue < 0.15f || obj.DominantHue > 0.85f) // Reddish
                return DetectionClass.Zombie;

            return DetectionClass.Unknown;
        }

        if (!obj.IsMoving && obj.Saturation > 0.5f)
        {
            // Static + colorful = likely item
            if (obj.DominantHue > 0.25f && obj.DominantHue < 0.45f) // Greenish
                return DetectionClass.HealthKit;

            if (obj.DominantHue < 0.15f) // Reddish
                return DetectionClass.AmmoBox;

            return DetectionClass.Loot;
        }

        return DetectionClass.Unknown;
    }

    private void ComputeScores(GradientObjectData obj)
    {
        // Threat score based on movement and approach
        if (obj.IsMoving)
        {
            ThreatScore = 0.3f + (obj.Velocity.Y > 0 ? 0.3f : 0) + obj.Speed * 0.4f;
        }

        // Opportunity based on color and stationary
        if (!obj.IsMoving && obj.Saturation > 0.3f)
        {
            OpportunityScore = 0.5f + obj.Saturation * 0.3f;
        }

        Priority = ComputePriority();
    }

    private void ComputeScoresFromClass()
    {
        ThreatScore = Class switch
        {
            DetectionClass.Zombie => 0.8f,
            DetectionClass.FastZombie => 0.9f,
            DetectionClass.TankZombie => 0.95f,
            DetectionClass.Enemy => 0.85f,
            _ => Class.IsThreat() ? 0.7f : 0.1f
        };

        OpportunityScore = Class switch
        {
            DetectionClass.HealthKit => 0.9f,
            DetectionClass.AmmoBox => 0.85f,
            DetectionClass.WeaponPickup => 0.8f,
            DetectionClass.Loot => 0.7f,
            _ => Class.IsItem() ? 0.6f : 0.05f
        };

        Priority = ComputePriority();
    }

    private void ComputeScoresFromTracking()
    {
        // Increase threat if approaching
        if (IsApproaching && IsThreat)
        {
            ThreatScore = Math.Min(1f, ThreatScore + Speed * 0.1f);
        }

        Priority = ComputePriority();
    }

    private float ComputePriority()
    {
        float basePriority = Math.Max(ThreatScore * 1.2f, OpportunityScore);

        // Boost for approaching threats
        if (IsThreat && IsApproaching)
            basePriority *= 1.3f;

        // Boost for close objects
        if (Position.Y > 400) // Lower on screen = closer
            basePriority *= 1.1f;

        // Confidence factor
        basePriority *= (0.5f + Confidence * 0.5f);

        return Math.Clamp(basePriority, 0, 1);
    }

    private void ComputeRenderStyle()
    {
        // Color based on classification
        RenderColor = Class switch
        {
            DetectionClass.Zombie => Color.FromArgb(255, 220, 50, 50),
            DetectionClass.FastZombie => Color.FromArgb(255, 255, 100, 50),
            DetectionClass.TankZombie => Color.FromArgb(255, 200, 0, 0),
            DetectionClass.Enemy => Color.FromArgb(255, 255, 50, 100),
            DetectionClass.HealthKit => Color.FromArgb(255, 50, 255, 50),
            DetectionClass.AmmoBox => Color.FromArgb(255, 255, 200, 50),
            DetectionClass.WeaponPickup => Color.FromArgb(255, 200, 150, 255),
            DetectionClass.Loot => Color.FromArgb(255, 100, 200, 255),
            DetectionClass.Survivor => Color.FromArgb(255, 50, 150, 255),
            DetectionClass.Player => Color.FromArgb(255, 50, 255, 255),
            _ => Color.FromArgb(200, 180, 180, 180)
        };

        // Brighten if high priority
        if (Priority > 0.7f)
        {
            RenderColor = Color.FromArgb(
                255,
                Math.Min(255, RenderColor.R + 30),
                Math.Min(255, RenderColor.G + 30),
                Math.Min(255, RenderColor.B + 30));
        }

        // Transparency based on confidence
        RenderAlpha = 0.5f + Confidence * 0.5f;

        // Apply trust level to color intensity
        if (TrustLevel == TrustLevel.Unknown)
        {
            RenderColor = Color.FromArgb(
                RenderColor.A,
                RenderColor.R / 2,
                RenderColor.G / 2,
                RenderColor.B / 2);
        }
    }

    public override string ToString()
    {
        return $"[{TrackId}] {ClassName} ({Confidence:P0}) @ {Position} " +
               $"trust={TrustLevel} threat={ThreatScore:F2} opp={OpportunityScore:F2}";
    }
}

/// <summary>
/// Trust level from authority certification.
/// </summary>
public enum TrustLevel
{
    Unknown = 0,
    Provisional = 1,
    Certified = 2,
    Trusted = 3,
    Absolute = 4
}

/// <summary>
/// Result of authority certification.
/// </summary>
public sealed class AuthorityCertification
{
    public TrustLevel Level { get; init; }
    public string? CertifiedClass { get; init; }
    public float TrustScore { get; init; }
    public float ThreatModifier { get; init; } = 1f;
    public float OpportunityModifier { get; init; } = 1f;
    public float BaseThreat { get; init; }
    public float BaseOpportunity { get; init; }
    public string? CertificationReason { get; init; }

    public static AuthorityCertification Unknown => new()
    {
        Level = TrustLevel.Unknown,
        TrustScore = 0.3f
    };
}

/// <summary>
/// Gradient object data for detection creation.
/// </summary>
public sealed class GradientObjectData
{
    public BoundingBox BoundingBox { get; init; }
    public Vector2 Velocity { get; init; }
    public float Confidence { get; init; }
    public float AspectRatio { get; init; }
    public float DominantHue { get; init; }
    public float Saturation { get; init; }
    public float Speed { get; init; }
    public bool IsMoving => Speed > 0.3f;
}

/// <summary>
/// Detection class enumeration with extension methods.
/// </summary>
public enum DetectionClass
{
    Unknown = 0,

    // Threats
    Zombie = 100,
    FastZombie = 101,
    TankZombie = 102,
    BossZombie = 103,
    Enemy = 110,
    Hostile = 111,

    // Items
    HealthKit = 200,
    AmmoBox = 201,
    WeaponPickup = 202,
    Loot = 203,
    Supply = 204,
    Armor = 205,

    // Characters
    Survivor = 300,
    Player = 301,
    Teammate = 302,
    NPC = 303,

    // Environment
    Door = 400,
    Barrier = 401,
    Objective = 402
}

public static class DetectionClassExtensions
{
    public static bool IsThreat(this DetectionClass c) => c is >= DetectionClass.Zombie and < DetectionClass.HealthKit;
    public static bool IsItem(this DetectionClass c) => c is >= DetectionClass.HealthKit and < DetectionClass.Survivor;
    public static bool IsCharacter(this DetectionClass c) => c is >= DetectionClass.Survivor and < DetectionClass.Door;
    public static bool IsEnvironment(this DetectionClass c) => c >= DetectionClass.Door;
}
