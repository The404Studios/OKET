using OKET.Core.Gradients;
using OKET.Core.Types;

namespace OKET.Core.Intelligence;

/// <summary>
/// Tracks intelligent detections across frames.
///
/// Responsibilities:
/// - Assign consistent track IDs
/// - Compute velocity from position changes
/// - Handle occlusions and re-identification
/// - Merge gradient objects into detections
///
/// Uses a combination of:
/// - IoU (Intersection over Union) for spatial matching
/// - Motion prediction (Kalman-like)
/// - Appearance similarity (color, shape)
/// </summary>
public sealed class IntelligenceTracker
{
    private readonly Dictionary<int, TrackedObject> _tracked = new();
    private readonly IntelligenceConfig _config;
    private int _nextTrackId = 1;

    public int ActiveTracks => _tracked.Count;

    public IntelligenceTracker(IntelligenceConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Track objects from gradient field.
    /// </summary>
    public List<IntelligentDetection> Track(GradientField field, long frameId)
    {
        // Get candidate objects from gradient field
        var candidates = ExtractCandidates(field, frameId);

        // Match candidates to existing tracks
        var (matched, unmatched, lost) = MatchCandidates(candidates, frameId);

        // Update matched tracks
        var detections = new List<IntelligentDetection>();
        foreach (var (trackId, candidate) in matched)
        {
            var track = _tracked[trackId];
            track.Update(candidate, frameId);
            detections.Add(track.ToDetection(frameId));
        }

        // Create new tracks for unmatched candidates
        foreach (var candidate in unmatched)
        {
            if (candidate.Confidence < _config.MinConfidence) continue;
            if (candidate.Area < _config.MinArea) continue;
            if (_tracked.Count >= _config.MaxTrackedObjects) break;

            var trackId = _nextTrackId++;
            var track = new TrackedObject(trackId, candidate, frameId);
            _tracked[trackId] = track;
            detections.Add(track.ToDetection(frameId));
        }

        // Handle lost tracks
        foreach (var trackId in lost)
        {
            var track = _tracked[trackId];
            track.MarkLost(frameId);

            if (track.FramesSinceSeen > _config.MaxLostFrames)
            {
                _tracked.Remove(trackId);
            }
        }

        return detections;
    }

    /// <summary>
    /// Ingest an external detection (from YOLO/ONNX).
    /// </summary>
    public void IngestExternal(IntelligentDetection external)
    {
        // Try to match with existing track
        float bestIoU = 0;
        int bestTrackId = -1;

        foreach (var (trackId, track) in _tracked)
        {
            float iou = track.PredictedBox.IoU(external.BoundingBox);
            if (iou > bestIoU && iou > _config.TrackingIoUThreshold)
            {
                bestIoU = iou;
                bestTrackId = trackId;
            }
        }

        if (bestTrackId >= 0)
        {
            // Update existing track
            var track = _tracked[bestTrackId];
            track.UpdateFromExternal(external);
        }
        else
        {
            // Create new track
            var trackId = external.TrackId > 0 ? external.TrackId : _nextTrackId++;
            _tracked[trackId] = TrackedObject.FromExternal(trackId, external);
        }
    }

    /// <summary>
    /// Extract candidate objects from gradient field.
    /// </summary>
    private List<CandidateObject> ExtractCandidates(GradientField field, long frameId)
    {
        var candidates = new List<CandidateObject>();
        var activeCells = field.FindActiveCells(_config.MinConfidence * 0.5f).ToList();

        if (activeCells.Count == 0) return candidates;

        // Cluster active cells into objects
        var clusters = ClusterCells(activeCells, field);

        foreach (var cluster in clusters)
        {
            if (cluster.Count < 2) continue;

            var candidate = BuildCandidate(cluster, field);
            if (candidate.Area >= _config.MinArea)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Cluster cells into coherent objects using flood-fill with similarity.
    /// </summary>
    private List<List<(int gx, int gy, float activity)>> ClusterCells(
        List<(int gx, int gy, float activity)> cells,
        GradientField field)
    {
        var clusters = new List<List<(int gx, int gy, float activity)>>();
        var assigned = new HashSet<(int, int)>();

        // Sort by activity (process highest first)
        var sorted = cells.OrderByDescending(c => c.activity).ToList();

        foreach (var seed in sorted)
        {
            if (assigned.Contains((seed.gx, seed.gy))) continue;

            var cluster = new List<(int gx, int gy, float activity)>();
            var queue = new Queue<(int gx, int gy)>();
            queue.Enqueue((seed.gx, seed.gy));

            var seedSample = field.GetSample(seed.gx, seed.gy);

            while (queue.Count > 0 && cluster.Count < 500)
            {
                var (cx, cy) = queue.Dequeue();
                if (assigned.Contains((cx, cy))) continue;

                var cell = cells.FirstOrDefault(c => c.gx == cx && c.gy == cy);
                if (cell.activity < 0.03f) continue;

                // Check similarity to seed
                var sample = field.GetSample(cx, cy);
                float similarity = seedSample.SimilarityTo(sample);
                if (similarity < 0.4f) continue;

                cluster.Add(cell);
                assigned.Add((cx, cy));

                // Add neighbors
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var neighbor = (cx + dx, cy + dy);
                        if (!assigned.Contains(neighbor))
                        {
                            queue.Enqueue(neighbor);
                        }
                    }
                }
            }

            if (cluster.Count >= 2)
            {
                clusters.Add(cluster);
            }
        }

        return clusters;
    }

    /// <summary>
    /// Build candidate object from cluster of cells.
    /// </summary>
    private CandidateObject BuildCandidate(
        List<(int gx, int gy, float activity)> cluster,
        GradientField field)
    {
        int minX = int.MaxValue, maxX = int.MinValue;
        int minY = int.MaxValue, maxY = int.MinValue;
        float sumVx = 0, sumVy = 0;
        float sumH = 0, sumS = 0, sumV = 0;
        float sumConf = 0;
        int count = 0;

        foreach (var (gx, gy, activity) in cluster)
        {
            var sample = field.GetSample(gx, gy);

            minX = Math.Min(minX, gx);
            maxX = Math.Max(maxX, gx);
            minY = Math.Min(minY, gy);
            maxY = Math.Max(maxY, gy);

            sumVx += sample.FlowX;
            sumVy += sample.FlowY;
            sumH += sample.Hue;
            sumS += sample.Saturation;
            sumV += sample.Value;
            sumConf += activity;
            count++;
        }

        if (count == 0) count = 1;

        // Convert grid coordinates to pixel coordinates
        float x = minX * field.CellSize;
        float y = minY * field.CellSize;
        float width = (maxX - minX + 1) * field.CellSize;
        float height = (maxY - minY + 1) * field.CellSize;

        return new CandidateObject
        {
            BoundingBox = new BoundingBox(x, y, width, height),
            Velocity = new Vector2(sumVx / count * field.CellSize, sumVy / count * field.CellSize),
            Confidence = sumConf / count,
            DominantHue = sumH / count,
            Saturation = sumS / count,
            Value = sumV / count,
            Area = width * height,
            AspectRatio = height / Math.Max(1, width),
            CellCount = count
        };
    }

    /// <summary>
    /// Match candidates to existing tracks.
    /// </summary>
    private (List<(int trackId, CandidateObject candidate)> matched,
             List<CandidateObject> unmatched,
             List<int> lost) MatchCandidates(
        List<CandidateObject> candidates,
        long frameId)
    {
        var matched = new List<(int, CandidateObject)>();
        var unmatched = new List<CandidateObject>(candidates);
        var matchedTracks = new HashSet<int>();

        // Build cost matrix (IoU-based)
        foreach (var (trackId, track) in _tracked)
        {
            float bestIoU = 0;
            CandidateObject? bestCandidate = null;

            foreach (var candidate in unmatched)
            {
                float iou = track.PredictedBox.IoU(candidate.BoundingBox);
                if (iou > bestIoU && iou > _config.TrackingIoUThreshold)
                {
                    // Also check appearance similarity
                    float appearance = ComputeAppearanceSimilarity(track, candidate);
                    float score = iou * 0.7f + appearance * 0.3f;

                    if (score > bestIoU)
                    {
                        bestIoU = score;
                        bestCandidate = candidate;
                    }
                }
            }

            if (bestCandidate != null)
            {
                matched.Add((trackId, bestCandidate));
                unmatched.Remove(bestCandidate);
                matchedTracks.Add(trackId);
            }
        }

        // Find lost tracks
        var lost = _tracked.Keys.Where(id => !matchedTracks.Contains(id)).ToList();

        return (matched, unmatched, lost);
    }

    /// <summary>
    /// Compute appearance similarity between track and candidate.
    /// </summary>
    private static float ComputeAppearanceSimilarity(TrackedObject track, CandidateObject candidate)
    {
        // Hue similarity (circular)
        float hueDiff = Math.Min(
            Math.Abs(track.DominantHue - candidate.DominantHue),
            1f - Math.Abs(track.DominantHue - candidate.DominantHue));
        float hueSim = 1f - hueDiff;

        // Saturation similarity
        float satSim = 1f - Math.Abs(track.Saturation - candidate.Saturation);

        // Size similarity
        float sizeRatio = track.Area > 0 ? candidate.Area / track.Area : 1f;
        float sizeSim = 1f - Math.Abs(1f - sizeRatio);

        return hueSim * 0.4f + satSim * 0.3f + sizeSim * 0.3f;
    }

    public void Reset()
    {
        _tracked.Clear();
        _nextTrackId = 1;
    }
}

/// <summary>
/// Internal tracked object state.
/// </summary>
internal sealed class TrackedObject
{
    public int TrackId { get; }
    public BoundingBox CurrentBox { get; private set; }
    public BoundingBox PredictedBox { get; private set; }
    public Vector2 Velocity { get; private set; }
    public Vector2 Acceleration { get; private set; }
    public float Confidence { get; private set; }
    public float DominantHue { get; private set; }
    public float Saturation { get; private set; }
    public float Area { get; private set; }

    public long FirstSeenFrame { get; }
    public long LastSeenFrame { get; private set; }
    public int FramesSinceSeen { get; private set; }

    public DetectionClass Class { get; private set; } = DetectionClass.Unknown;
    public bool IsLost { get; private set; }

    public TrackedObject(int trackId, CandidateObject initial, long frameId)
    {
        TrackId = trackId;
        FirstSeenFrame = frameId;
        LastSeenFrame = frameId;
        CurrentBox = initial.BoundingBox;
        PredictedBox = initial.BoundingBox;
        Velocity = initial.Velocity;
        Confidence = initial.Confidence;
        DominantHue = initial.DominantHue;
        Saturation = initial.Saturation;
        Area = initial.Area;
    }

    public static TrackedObject FromExternal(int trackId, IntelligentDetection external)
    {
        return new TrackedObject(trackId, new CandidateObject
        {
            BoundingBox = external.BoundingBox,
            Velocity = external.Velocity,
            Confidence = external.Confidence,
            Area = external.Area
        }, external.FirstSeenFrame)
        {
            Class = external.Class
        };
    }

    public void Update(CandidateObject candidate, long frameId)
    {
        var oldCenter = CurrentBox.Center;
        var newCenter = candidate.BoundingBox.Center;

        // Update motion
        var oldVelocity = Velocity;
        Velocity = (newCenter - oldCenter);
        Acceleration = Velocity - oldVelocity;

        // Update box
        CurrentBox = candidate.BoundingBox;
        PredictedBox = new BoundingBox(
            candidate.BoundingBox.X + Velocity.X,
            candidate.BoundingBox.Y + Velocity.Y,
            candidate.BoundingBox.Width,
            candidate.BoundingBox.Height);

        // Update appearance
        DominantHue = DominantHue * 0.8f + candidate.DominantHue * 0.2f;
        Saturation = Saturation * 0.8f + candidate.Saturation * 0.2f;
        Area = Area * 0.9f + candidate.Area * 0.1f;
        Confidence = Confidence * 0.7f + candidate.Confidence * 0.3f;

        LastSeenFrame = frameId;
        FramesSinceSeen = 0;
        IsLost = false;
    }

    public void UpdateFromExternal(IntelligentDetection external)
    {
        var oldCenter = CurrentBox.Center;
        var newCenter = external.BoundingBox.Center;

        Velocity = (newCenter - oldCenter);
        CurrentBox = external.BoundingBox;
        PredictedBox = new BoundingBox(
            external.BoundingBox.X + Velocity.X,
            external.BoundingBox.Y + Velocity.Y,
            external.BoundingBox.Width,
            external.BoundingBox.Height);
        Confidence = external.Confidence;
        Class = external.Class;
        FramesSinceSeen = 0;
        IsLost = false;
    }

    public void MarkLost(long frameId)
    {
        FramesSinceSeen = (int)(frameId - LastSeenFrame);
        IsLost = true;

        // Update prediction based on motion
        PredictedBox = new BoundingBox(
            PredictedBox.X + Velocity.X,
            PredictedBox.Y + Velocity.Y,
            PredictedBox.Width,
            PredictedBox.Height);

        // Decay confidence
        Confidence *= 0.95f;
    }

    public IntelligentDetection ToDetection(long frameId)
    {
        var gradientData = new GradientObjectData
        {
            BoundingBox = CurrentBox,
            Velocity = Velocity,
            Confidence = Confidence,
            AspectRatio = CurrentBox.Height / Math.Max(1, CurrentBox.Width),
            DominantHue = DominantHue,
            Saturation = Saturation,
            Speed = Velocity.Length()
        };

        var detection = IntelligentDetection.FromGradient(gradientData, TrackId, FirstSeenFrame);

        // Update with tracking info
        if (frameId != FirstSeenFrame)
        {
            detection.UpdateTracking(CurrentBox, frameId, Confidence);
        }

        return detection;
    }
}

/// <summary>
/// Candidate object from gradient clustering.
/// </summary>
internal sealed class CandidateObject
{
    public BoundingBox BoundingBox { get; init; }
    public Vector2 Velocity { get; init; }
    public float Confidence { get; init; }
    public float DominantHue { get; init; }
    public float Saturation { get; init; }
    public float Value { get; init; }
    public float Area { get; init; }
    public float AspectRatio { get; init; }
    public int CellCount { get; init; }
}
