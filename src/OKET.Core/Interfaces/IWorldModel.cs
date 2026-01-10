using OKET.Core.Types;
using OKET.Core.State;
using OKET.Core.Detection;

namespace OKET.Core.Interfaces;

/// <summary>
/// World model that maintains persistent state across frames.
/// Handles tracking, predictions, and spatial memory.
/// </summary>
public interface IWorldModel
{
    /// <summary>
    /// Update the world model with new observations.
    /// </summary>
    void Update(GameState state);

    /// <summary>
    /// Get tracked targets with predicted positions.
    /// </summary>
    IReadOnlyList<TrackedTarget> TrackedTargets { get; }

    /// <summary>
    /// Get the best target to engage.
    /// </summary>
    TrackedTarget? PrimaryTarget { get; }

    /// <summary>
    /// Predict where a target will be in the future.
    /// </summary>
    Vector2? PredictPosition(int trackId, float deltaTimeMs);

    /// <summary>
    /// Record a significant location (death, good spot, etc.).
    /// </summary>
    void RecordLocation(string type, Vector2 position);

    /// <summary>
    /// Reset tracking state (e.g., on respawn).
    /// </summary>
    void Reset();
}

/// <summary>
/// A target being tracked across frames.
/// </summary>
public sealed class TrackedTarget
{
    public int TrackId { get; init; }
    public DetectionClass Class { get; init; }
    public Vector2 Position { get; set; }
    public Vector2 Velocity { get; set; }
    public float Confidence { get; set; }
    public int FramesSinceLastSeen { get; set; }
    public float Priority { get; set; }
    public BoundingBox LastBox { get; set; }

    public bool IsStale => FramesSinceLastSeen > 30; // ~1 second at 30fps
}
