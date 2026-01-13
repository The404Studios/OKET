using OKET.Core.Types;
using OKET.Core.State;
using OKET.Core.Detection;

namespace OKET.Core.Prediction;

/// <summary>
/// Simple 1-step frame predictor using velocity estimation.
/// No ML required - pure physics-based prediction.
/// </summary>
public sealed class FramePredictor
{
    private readonly int _historySize;
    private readonly Queue<FrameSnapshot> _history = new();
    private readonly Dictionary<int, EntityTrack> _entityTracks = new();

    /// <summary>Last prediction error for debugging.</summary>
    public float LastPredictionError { get; private set; }

    public FramePredictor(int historySize = 5)
    {
        _historySize = historySize;
    }

    /// <summary>
    /// Record current frame state.
    /// </summary>
    public void RecordFrame(GameState state)
    {
        var snapshot = new FrameSnapshot
        {
            FrameId = state.FrameId,
            Timestamp = state.Timestamp,
            PlayerHealth = state.Hud.Health,
            ThreatCount = state.ThreatsInFov,
            NearestThreatDistance = state.NearestThreatDistance
        };

        // Track each detected entity
        foreach (var detection in state.Detections.Detections)
        {
            var position = detection.Box.Center;

            if (_entityTracks.TryGetValue(detection.TrackId, out var track))
            {
                // Update existing track
                track.Update(position, state.FrameId);
            }
            else
            {
                // New entity
                _entityTracks[detection.TrackId] = new EntityTrack(detection.TrackId, position, state.FrameId);
            }

            snapshot.EntityPositions[detection.TrackId] = position;
        }

        // Add to history
        _history.Enqueue(snapshot);
        while (_history.Count > _historySize)
        {
            _history.Dequeue();
        }

        // Prune stale tracks (not seen in 30 frames)
        var staleIds = _entityTracks
            .Where(kvp => state.FrameId - kvp.Value.LastSeenFrame > 30)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var id in staleIds)
        {
            _entityTracks.Remove(id);
        }
    }

    /// <summary>
    /// Predict state 1 frame ahead.
    /// </summary>
    public FramePrediction PredictNext(GameState currentState)
    {
        var prediction = new FramePrediction
        {
            FrameId = currentState.FrameId + 1,
            PredictedThreatCount = currentState.ThreatsInFov,
            PredictedHealth = currentState.Hud.Health
        };

        // Predict entity positions using velocity
        foreach (var (trackId, track) in _entityTracks)
        {
            var velocity = track.EstimatedVelocity;
            var predictedPos = track.LastPosition + velocity;

            prediction.PredictedPositions[trackId] = new EntityPrediction
            {
                TrackId = trackId,
                CurrentPosition = track.LastPosition,
                PredictedPosition = predictedPos,
                Velocity = velocity,
                Confidence = track.VelocityConfidence
            };
        }

        // Predict nearest threat distance
        if (currentState.Detections.PrimaryThreat != null)
        {
            var threatId = currentState.Detections.PrimaryThreat.TrackId;
            if (prediction.PredictedPositions.TryGetValue(threatId, out var threatPred))
            {
                // Estimate player position (screen center)
                var playerPos = currentState.ScreenSize / 2f;
                prediction.PredictedNearestThreatDistance =
                    Vector2.Distance(playerPos, threatPred.PredictedPosition);
            }
        }

        // Estimate threat delta (are threats getting closer?)
        prediction.ThreatDelta = EstimateThreatDelta(currentState);

        return prediction;
    }

    /// <summary>
    /// Evaluate how accurate our last prediction was.
    /// </summary>
    public void EvaluatePrediction(FramePrediction prediction, GameState actualState)
    {
        float totalError = 0f;
        int count = 0;

        foreach (var (trackId, pred) in prediction.PredictedPositions)
        {
            var actualDetection = actualState.Detections.Detections
                .FirstOrDefault(d => d.TrackId == trackId);

            if (actualDetection != null)
            {
                var actualPos = actualDetection.Box.Center;
                var error = Vector2.Distance(pred.PredictedPosition, actualPos);
                totalError += error;
                count++;

                // Update track with error feedback
                if (_entityTracks.TryGetValue(trackId, out var track))
                {
                    track.RecordPredictionError(error);
                }
            }
        }

        LastPredictionError = count > 0 ? totalError / count : 0f;
    }

    /// <summary>
    /// Estimate if threats are approaching or retreating.
    /// </summary>
    private float EstimateThreatDelta(GameState state)
    {
        if (_history.Count < 2)
            return 0f;

        var prev = _history.ElementAt(_history.Count - 2);
        var curr = _history.Last();

        // Compare nearest threat distance
        float distDelta = curr.NearestThreatDistance - prev.NearestThreatDistance;

        // Negative = threats getting closer
        return -distDelta / 100f; // Normalize to reasonable range
    }

    /// <summary>
    /// Get predicted collision time with nearest threat.
    /// </summary>
    public float? PredictCollisionTime(GameState state)
    {
        var threat = state.Detections.PrimaryThreat;
        if (threat == null)
            return null;

        if (!_entityTracks.TryGetValue(threat.TrackId, out var track))
            return null;

        var velocity = track.EstimatedVelocity;
        if (velocity.Length < 1f)
            return null; // Not moving toward us

        var playerPos = state.ScreenSize / 2f;
        var threatPos = threat.Box.Center;
        var distance = Vector2.Distance(playerPos, threatPos);

        // Simple linear estimate
        var approachSpeed = velocity.Length;
        if (approachSpeed < 1f)
            return null;

        return distance / approachSpeed; // Frames until collision
    }

    /// <summary>
    /// Predict reward delta for a candidate action.
    /// </summary>
    public ActionPrediction PredictActionOutcome(GameState state, ActionCandidate action)
    {
        var prediction = new ActionPrediction
        {
            Action = action,
            PredictedReward = 0f
        };

        // Base reward: staying alive
        prediction.PredictedReward += 0.01f;

        // Movement toward goal
        if (action.MovementDirection != Vector2.Zero)
        {
            // Check if moving toward or away from threats
            var threat = state.Detections.PrimaryThreat;
            if (threat != null)
            {
                var threatDir = (threat.Box.Center - state.ScreenSize / 2f).Normalized;
                var moveDir = action.MovementDirection.Normalized;
                var dot = Vector2.Dot(threatDir, moveDir);

                if (state.Hud.IsLowHealth)
                {
                    // Moving away from threat is good when low health
                    prediction.PredictedReward += -dot * 0.3f;
                }
                else
                {
                    // Moving toward threat is acceptable when healthy
                    prediction.PredictedReward += dot * 0.1f;
                }
            }
        }

        // Attacking
        if (action.IsAttacking && state.Aim.IsOnTarget)
        {
            prediction.PredictedReward += 0.5f; // High reward for hitting
        }
        else if (action.IsAttacking && !state.Aim.IsOnTarget)
        {
            prediction.PredictedReward -= 0.1f; // Penalty for missing
        }

        // Confidence based on prediction history
        prediction.Confidence = Math.Max(0.3f, 1f - (LastPredictionError / 100f));

        return prediction;
    }
}

/// <summary>
/// Snapshot of a single frame for history.
/// </summary>
internal sealed class FrameSnapshot
{
    public long FrameId { get; init; }
    public DateTime Timestamp { get; init; }
    public int PlayerHealth { get; init; }
    public int ThreatCount { get; init; }
    public float NearestThreatDistance { get; init; }
    public Dictionary<int, Vector2> EntityPositions { get; } = new();
}

/// <summary>
/// Track for a single entity over time.
/// </summary>
internal sealed class EntityTrack
{
    public int TrackId { get; }
    public Vector2 LastPosition { get; private set; }
    public Vector2 EstimatedVelocity { get; private set; }
    public float VelocityConfidence { get; private set; } = 0.5f;
    public long LastSeenFrame { get; private set; }

    private readonly Queue<(Vector2 pos, long frame)> _positionHistory = new();
    private float _recentPredictionError;

    public EntityTrack(int trackId, Vector2 position, long frame)
    {
        TrackId = trackId;
        LastPosition = position;
        LastSeenFrame = frame;
        _positionHistory.Enqueue((position, frame));
    }

    public void Update(Vector2 position, long frame)
    {
        // Calculate velocity from last position
        if (LastSeenFrame > 0 && frame > LastSeenFrame)
        {
            var frameDelta = frame - LastSeenFrame;
            var newVelocity = (position - LastPosition) / frameDelta;

            // Smooth velocity with exponential moving average
            EstimatedVelocity = EstimatedVelocity * 0.7f + newVelocity * 0.3f;
        }

        LastPosition = position;
        LastSeenFrame = frame;

        // Maintain history
        _positionHistory.Enqueue((position, frame));
        while (_positionHistory.Count > 10)
        {
            _positionHistory.Dequeue();
        }

        // Update confidence based on recent prediction accuracy
        VelocityConfidence = Math.Clamp(1f - (_recentPredictionError / 50f), 0.1f, 1f);
    }

    public void RecordPredictionError(float error)
    {
        _recentPredictionError = _recentPredictionError * 0.8f + error * 0.2f;
    }
}

/// <summary>
/// Prediction for next frame.
/// </summary>
public sealed class FramePrediction
{
    public long FrameId { get; init; }
    public int PredictedThreatCount { get; init; }
    public int PredictedHealth { get; init; }
    public float PredictedNearestThreatDistance { get; set; }
    public float ThreatDelta { get; set; } // Positive = threats approaching
    public Dictionary<int, EntityPrediction> PredictedPositions { get; } = new();
}

/// <summary>
/// Prediction for a single entity.
/// </summary>
public readonly struct EntityPrediction
{
    public int TrackId { get; init; }
    public Vector2 CurrentPosition { get; init; }
    public Vector2 PredictedPosition { get; init; }
    public Vector2 Velocity { get; init; }
    public float Confidence { get; init; }
}

/// <summary>
/// Candidate action for prediction.
/// </summary>
public sealed class ActionCandidate
{
    public Vector2 MovementDirection { get; init; }
    public bool IsAttacking { get; init; }
    public bool IsReloading { get; init; }
    public string Description { get; init; } = "";
}

/// <summary>
/// Predicted outcome of an action.
/// </summary>
public sealed class ActionPrediction
{
    public ActionCandidate Action { get; init; } = null!;
    public float PredictedReward { get; set; }
    public float Confidence { get; set; }
}
