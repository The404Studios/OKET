using OKET.Core.Types;
using OKET.Core.State;
using OKET.Core.Detection;
using OKET.Core.Interfaces;

namespace OKET.Agent.Memory;

/// <summary>
/// Maintains persistent world state across frames.
/// Handles target tracking, prediction, and spatial memory.
/// </summary>
public sealed class WorldModel : IWorldModel
{
    private readonly Dictionary<int, TrackedTarget> _targets = new();
    private readonly List<(string Type, Vector2 Position, DateTime Time)> _locations = new();
    private int _nextTrackId = 1;
    private const int MaxStaleFrames = 45; // ~1.5 seconds at 30fps

    public IReadOnlyList<TrackedTarget> TrackedTargets =>
        _targets.Values.Where(t => !t.IsStale).OrderByDescending(t => t.Priority).ToList();

    public TrackedTarget? PrimaryTarget =>
        TrackedTargets.FirstOrDefault();

    public void Update(GameState state)
    {
        // Increment age of all tracked targets
        foreach (var target in _targets.Values)
        {
            target.FramesSinceLastSeen++;
        }

        // Match new detections to existing tracks
        var matchedDetections = new HashSet<Detection>();

        foreach (var detection in state.Detections.Detections.Where(d => d.IsThreat))
        {
            var match = FindBestMatch(detection);

            if (match != null)
            {
                // Update existing track
                UpdateTrack(match, detection);
                matchedDetections.Add(detection);
            }
        }

        // Create new tracks for unmatched detections
        foreach (var detection in state.Detections.Detections.Where(d => d.IsThreat))
        {
            if (!matchedDetections.Contains(detection))
            {
                CreateTrack(detection);
            }
        }

        // Remove stale tracks
        var staleIds = _targets
            .Where(kvp => kvp.Value.FramesSinceLastSeen > MaxStaleFrames)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var id in staleIds)
        {
            _targets.Remove(id);
        }

        // Clean up old location memory
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        _locations.RemoveAll(l => l.Time < cutoff);
    }

    private TrackedTarget? FindBestMatch(Detection detection)
    {
        TrackedTarget? bestMatch = null;
        float bestScore = float.MaxValue;

        foreach (var target in _targets.Values)
        {
            if (target.Class != detection.Class)
                continue;

            // Predict where target should be
            var predicted = PredictPosition(target.TrackId, target.FramesSinceLastSeen * 33.33f);
            var position = predicted ?? target.Position;

            // Calculate match score (lower is better)
            float dist = Vector2.Distance(position, detection.Box.Center);
            float sizeDiff = Math.Abs(target.LastBox.Area - detection.Box.Area) / Math.Max(target.LastBox.Area, 1);

            float score = dist + sizeDiff * 50;

            // Must be within reasonable distance
            if (dist > 200) continue;

            if (score < bestScore)
            {
                bestScore = score;
                bestMatch = target;
            }
        }

        return bestMatch;
    }

    private void UpdateTrack(TrackedTarget target, Detection detection)
    {
        // Calculate velocity using exponential moving average
        float dt = Math.Max(target.FramesSinceLastSeen * 33.33f, 1f);
        var newVelocity = (detection.Box.Center - target.Position) * (1000f / dt); // pixels per second

        // EMA for velocity smoothing
        const float alpha = 0.3f;
        target.Velocity = target.Velocity * (1 - alpha) + newVelocity * alpha;

        // Update position and metadata
        target.Position = detection.Box.Center;
        target.LastBox = detection.Box;
        target.Confidence = detection.Confidence;
        target.Priority = detection.Priority;
        target.FramesSinceLastSeen = 0;

        // Update detection's track ID
        detection.TrackId = target.TrackId;
        detection.Velocity = target.Velocity;
    }

    private void CreateTrack(Detection detection)
    {
        var trackId = _nextTrackId++;

        var target = new TrackedTarget
        {
            TrackId = trackId,
            Class = detection.Class,
            Position = detection.Box.Center,
            Velocity = Vector2.Zero,
            Confidence = detection.Confidence,
            Priority = detection.Priority,
            LastBox = detection.Box,
            FramesSinceLastSeen = 0
        };

        _targets[trackId] = target;
        detection.TrackId = trackId;
    }

    public Vector2? PredictPosition(int trackId, float deltaTimeMs)
    {
        if (!_targets.TryGetValue(trackId, out var target))
            return null;

        if (target.Velocity.LengthSquared < 0.001f)
            return target.Position;

        // Linear prediction
        float dt = deltaTimeMs / 1000f;
        return target.Position + target.Velocity * dt;
    }

    public void RecordLocation(string type, Vector2 position)
    {
        _locations.Add((type, position, DateTime.UtcNow));
    }

    public IEnumerable<Vector2> GetLocations(string type, int maxCount = 10)
    {
        return _locations
            .Where(l => l.Type == type)
            .OrderByDescending(l => l.Time)
            .Take(maxCount)
            .Select(l => l.Position);
    }

    public void Reset()
    {
        _targets.Clear();
        _nextTrackId = 1;
    }
}
