using OKET.Core.Types;

namespace OKET.Core.Detection;

/// <summary>
/// Estimates distance to detected objects based on bounding box size.
/// Uses perspective projection: larger objects = closer, smaller = farther.
/// </summary>
public sealed class DistanceEstimator
{
    private readonly Dictionary<DetectionClass, ReferenceSize> _referenceSizes;
    private readonly float _focalLength;
    private readonly Vector2 _screenSize;

    /// <summary>
    /// Initialize with screen size and optional custom focal length.
    /// </summary>
    public DistanceEstimator(Vector2 screenSize, float focalLength = 1000f)
    {
        _screenSize = screenSize;
        _focalLength = focalLength;

        // Reference sizes: expected pixel height at 1 meter distance
        // These should be calibrated per game
        _referenceSizes = new Dictionary<DetectionClass, ReferenceSize>
        {
            // Zombies - human-sized targets
            { DetectionClass.Zombie, new ReferenceSize(300f, 200f, 1.8f) },
            { DetectionClass.FastZombie, new ReferenceSize(280f, 180f, 1.6f) },
            { DetectionClass.PoisonZombie, new ReferenceSize(350f, 220f, 2.0f) },
            { DetectionClass.ZombieHead, new ReferenceSize(60f, 50f, 0.3f) },

            // Small creatures
            { DetectionClass.Headcrab, new ReferenceSize(80f, 100f, 0.4f) },

            // Players
            { DetectionClass.Player, new ReferenceSize(300f, 180f, 1.8f) },
            { DetectionClass.PlayerHead, new ReferenceSize(50f, 50f, 0.25f) },
            { DetectionClass.Teammate, new ReferenceSize(300f, 180f, 1.8f) },

            // Interactables - vary more in size
            { DetectionClass.Barricade, new ReferenceSize(200f, 300f, 1.5f) },
            { DetectionClass.BarricadeBoard, new ReferenceSize(100f, 150f, 0.8f) },
            { DetectionClass.AmmoCrate, new ReferenceSize(80f, 100f, 0.5f) },
            { DetectionClass.WeaponCrate, new ReferenceSize(100f, 120f, 0.6f) },
            { DetectionClass.HealthKit, new ReferenceSize(50f, 60f, 0.3f) },
            { DetectionClass.Door, new ReferenceSize(350f, 200f, 2.2f) },

            // Default for unknown
            { DetectionClass.Unknown, new ReferenceSize(150f, 150f, 1.0f) }
        };
    }

    /// <summary>
    /// Estimate distance to a detection based on bounding box size.
    /// </summary>
    public float EstimateDistance(Detection detection)
    {
        return EstimateDistance(detection.Class, detection.Box);
    }

    /// <summary>
    /// Estimate distance based on class and bounding box.
    /// </summary>
    public float EstimateDistance(DetectionClass detectionClass, BoundingBox box)
    {
        if (!_referenceSizes.TryGetValue(detectionClass, out var reference))
        {
            reference = _referenceSizes[DetectionClass.Unknown];
        }

        // Use height primarily, as width can vary with orientation
        float heightRatio = reference.HeightAt1m / box.Height;
        float widthRatio = reference.WidthAt1m / box.Width;

        // Weight height more than width (height is more consistent)
        float combinedRatio = heightRatio * 0.7f + widthRatio * 0.3f;

        // Distance = reference_size * focal_length / observed_size
        // Simplified: distance ≈ ratio (since we pre-baked the focal length into reference)
        float distance = combinedRatio * reference.RealWorldHeight;

        // Clamp to reasonable game distances
        return Math.Clamp(distance, 0.3f, 100f);
    }

    /// <summary>
    /// Update all detections in a result with estimated distances.
    /// </summary>
    public void UpdateDistances(DetectionResult result)
    {
        foreach (var detection in result.Detections)
        {
            if (!detection.EstimatedDistance.HasValue)
            {
                detection.EstimatedDistance = EstimateDistance(detection);
            }
        }
    }

    /// <summary>
    /// Get distance category for a distance value.
    /// </summary>
    public static DistanceCategory GetCategory(float distance)
    {
        return distance switch
        {
            < 2f => DistanceCategory.VeryClose,
            < 5f => DistanceCategory.Close,
            < 15f => DistanceCategory.Medium,
            < 30f => DistanceCategory.Far,
            _ => DistanceCategory.VeryFar
        };
    }

    /// <summary>
    /// Calculate time to reach a target at a given distance with given speed.
    /// </summary>
    public static float EstimateTimeToReach(float distance, float speedMetersPerSec = 3f)
    {
        if (speedMetersPerSec <= 0) return float.MaxValue;
        return distance / speedMetersPerSec;
    }

    /// <summary>
    /// Get detailed distance info for a detection.
    /// </summary>
    public DistanceInfo GetDistanceInfo(Detection detection)
    {
        float distance = EstimateDistance(detection);
        var category = GetCategory(distance);

        // Calculate screen-relative size
        float relativeHeight = detection.Box.Height / _screenSize.Y;
        float relativeWidth = detection.Box.Width / _screenSize.X;

        return new DistanceInfo
        {
            Distance = distance,
            Category = category,
            RelativeHeight = relativeHeight,
            RelativeWidth = relativeWidth,
            Confidence = CalculateDistanceConfidence(detection, distance),
            TimeToReach = EstimateTimeToReach(distance),
            IsDangerous = category <= DistanceCategory.Close && detection.IsThreat
        };
    }

    /// <summary>
    /// Calculate confidence in the distance estimate.
    /// </summary>
    private float CalculateDistanceConfidence(Detection detection, float distance)
    {
        float confidence = 1f;

        // Lower confidence for small boxes (more measurement error)
        if (detection.Box.Height < 30 || detection.Box.Width < 30)
        {
            confidence *= 0.7f;
        }

        // Lower confidence for very close or far (edge cases)
        if (distance < 1f || distance > 50f)
        {
            confidence *= 0.8f;
        }

        // Lower confidence for detection classes with variable size
        if (detection.Class == DetectionClass.Barricade ||
            detection.Class == DetectionClass.Unknown)
        {
            confidence *= 0.6f;
        }

        // Factor in detection confidence
        confidence *= detection.Confidence;

        return Math.Clamp(confidence, 0.1f, 1f);
    }

    /// <summary>
    /// Calibrate reference size for a class based on known distance.
    /// </summary>
    public void Calibrate(DetectionClass detectionClass, BoundingBox box, float knownDistance)
    {
        if (!_referenceSizes.ContainsKey(detectionClass))
            return;

        var current = _referenceSizes[detectionClass];

        // Calculate what the reference should be at 1m
        float newHeightRef = box.Height * knownDistance;
        float newWidthRef = box.Width * knownDistance;

        // Smooth update (don't completely replace)
        _referenceSizes[detectionClass] = new ReferenceSize(
            current.HeightAt1m * 0.8f + newHeightRef * 0.2f,
            current.WidthAt1m * 0.8f + newWidthRef * 0.2f,
            current.RealWorldHeight
        );
    }
}

/// <summary>
/// Reference size for a detection class.
/// </summary>
internal readonly struct ReferenceSize
{
    /// <summary>Expected height in pixels at 1 meter.</summary>
    public float HeightAt1m { get; }

    /// <summary>Expected width in pixels at 1 meter.</summary>
    public float WidthAt1m { get; }

    /// <summary>Real world height in meters.</summary>
    public float RealWorldHeight { get; }

    public ReferenceSize(float heightAt1m, float widthAt1m, float realWorldHeight)
    {
        HeightAt1m = heightAt1m;
        WidthAt1m = widthAt1m;
        RealWorldHeight = realWorldHeight;
    }
}

/// <summary>
/// Detailed distance information.
/// </summary>
public readonly struct DistanceInfo
{
    /// <summary>Estimated distance in meters.</summary>
    public float Distance { get; init; }

    /// <summary>Distance category.</summary>
    public DistanceCategory Category { get; init; }

    /// <summary>Box height relative to screen height [0, 1].</summary>
    public float RelativeHeight { get; init; }

    /// <summary>Box width relative to screen width [0, 1].</summary>
    public float RelativeWidth { get; init; }

    /// <summary>Confidence in the estimate [0, 1].</summary>
    public float Confidence { get; init; }

    /// <summary>Estimated time to reach in seconds (at walking speed).</summary>
    public float TimeToReach { get; init; }

    /// <summary>Whether this is a dangerous close threat.</summary>
    public bool IsDangerous { get; init; }

    public override string ToString() =>
        $"{Distance:F1}m ({Category}) [conf={Confidence:P0}]";
}

/// <summary>
/// Distance categories for classification.
/// </summary>
public enum DistanceCategory
{
    /// <summary>Less than 2 meters - immediate threat.</summary>
    VeryClose,

    /// <summary>2-5 meters - close range.</summary>
    Close,

    /// <summary>5-15 meters - medium range.</summary>
    Medium,

    /// <summary>15-30 meters - far range.</summary>
    Far,

    /// <summary>More than 30 meters - very far.</summary>
    VeryFar
}
