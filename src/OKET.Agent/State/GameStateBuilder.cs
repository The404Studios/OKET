using OKET.Core.Types;
using OKET.Core.State;
using OKET.Core.Detection;
using OKET.Core.Interfaces;

namespace OKET.Agent.State;

/// <summary>
/// Builds game state from raw perceptions.
/// Combines HUD data, detections, and previous state into a unified view.
/// </summary>
public sealed class GameStateBuilder : IStateBuilder
{
    private int _screenWidth = 1920;
    private int _screenHeight = 1080;

    private int _framesSinceHit;
    private int _framesSinceDamage;
    private int _lastHealth = 100;
    private Vector2 _lastTargetPosition;
    private float _trackingDuration;
    private int _lastTargetId = -1;

    // Stuck detection
    private readonly Queue<GameState> _recentStates = new();
    private const int StuckCheckFrames = 60;

    public Vector2 ScreenCenter => new(_screenWidth / 2f, _screenHeight / 2f);

    public void Configure(int width, int height)
    {
        _screenWidth = width;
        _screenHeight = height;
    }

    public GameState Build(Frame frame, HudState hud, DetectionResult detections, GameState? previousState)
    {
        _screenWidth = frame.Width;
        _screenHeight = frame.Height;

        // Update frame counters
        UpdateFrameCounters(hud, detections, previousState);

        // Build aim state
        var aimState = BuildAimState(detections, previousState);

        // Calculate nearest threat
        float nearestThreatDist = CalculateNearestThreatDistance(detections);

        // Check if stuck
        bool isStuck = CheckIfStuck(previousState);

        var state = new GameState
        {
            FrameId = frame.Id,
            Timestamp = frame.Timestamp,
            Hud = hud,
            Aim = aimState,
            Detections = detections,
            ScreenSize = new Vector2(_screenWidth, _screenHeight),
            NearestThreatDistance = nearestThreatDist,
            IsStuck = isStuck,
            FramesSinceHit = _framesSinceHit,
            FramesSinceDamage = _framesSinceDamage
        };

        // Track state for stuck detection
        _recentStates.Enqueue(state);
        if (_recentStates.Count > StuckCheckFrames)
            _recentStates.Dequeue();

        return state;
    }

    private void UpdateFrameCounters(HudState hud, DetectionResult detections, GameState? previousState)
    {
        // Hit detection from crosshair region
        // This would ideally check for hit markers but for now increment
        _framesSinceHit++;

        // Damage detection
        if (hud.Health < _lastHealth)
        {
            _framesSinceDamage = 0;
        }
        else
        {
            _framesSinceDamage++;
        }
        _lastHealth = hud.Health;

        // Reset on death/respawn
        if (hud.IsDead)
        {
            _framesSinceHit = 0;
            _framesSinceDamage = 0;
        }
    }

    private AimState BuildAimState(DetectionResult detections, GameState? previousState)
    {
        var crosshair = ScreenCenter;

        // Find the best target to track
        var target = SelectTarget(detections);

        if (target == null)
        {
            _lastTargetId = -1;
            _trackingDuration = 0;
            return AimState.NoTarget(crosshair);
        }

        // Calculate offset to target
        var aimPoint = target.GetAimPoint(preferHeadshot: true);
        var offset = aimPoint - crosshair;

        // Check if we're on target
        bool isOnTarget = offset.Length < AimState.OnTargetTolerance;

        // Update tracking duration
        if (target.TrackId == _lastTargetId || _lastTargetId == -1)
        {
            _trackingDuration += 33.33f; // ~30fps assumption
        }
        else
        {
            _trackingDuration = 0;
        }

        _lastTargetId = target.TrackId;
        _lastTargetPosition = aimPoint;

        return new AimState
        {
            CrosshairPosition = crosshair,
            Target = target,
            OffsetToTarget = offset,
            IsOnTarget = isOnTarget,
            TrackingDuration = _trackingDuration,
            HitConfirmed = _framesSinceHit < 10 // Recent hit
        };
    }

    private Detection? SelectTarget(DetectionResult detections)
    {
        if (detections.ThreatCount == 0)
            return null;

        // Get threats sorted by priority
        var threats = detections.Threats.ToList();
        if (threats.Count == 0)
            return null;

        // Prioritize current target if still visible (target stickiness)
        if (_lastTargetId >= 0)
        {
            var currentTarget = threats.FirstOrDefault(t => t.TrackId == _lastTargetId);
            if (currentTarget != null && currentTarget.Confidence > 0.3f)
            {
                // Only switch if new target is significantly better
                var best = threats[0];
                if (best.TrackId == _lastTargetId || best.Priority < currentTarget.Priority * 1.5f)
                {
                    return currentTarget;
                }
            }
        }

        return threats[0];
    }

    private float CalculateNearestThreatDistance(DetectionResult detections)
    {
        if (detections.ThreatCount == 0)
            return float.MaxValue;

        float minDist = float.MaxValue;
        var center = ScreenCenter;

        foreach (var detection in detections.Threats)
        {
            var dist = Vector2.Distance(center, detection.Box.Center);
            minDist = Math.Min(minDist, dist);
        }

        return minDist;
    }

    private bool CheckIfStuck(GameState? previousState)
    {
        if (_recentStates.Count < StuckCheckFrames)
            return false;

        // Check if state hasn't changed much despite likely movement commands
        // This is a simplified check - real implementation would compare
        // positions of detected landmarks

        var states = _recentStates.ToArray();

        // Check if threats have been in roughly the same position
        // (which would indicate we're not actually moving)
        var firstState = states[0];
        var lastState = states[^1];

        if (firstState.Detections.ThreatCount > 0 && lastState.Detections.ThreatCount > 0)
        {
            var firstThreat = firstState.Detections.PrimaryThreat;
            var lastThreat = lastState.Detections.PrimaryThreat;

            if (firstThreat != null && lastThreat != null &&
                firstThreat.Class == lastThreat.Class)
            {
                var posDelta = Vector2.Distance(firstThreat.Box.Center, lastThreat.Box.Center);
                // If threat position hasn't changed much over many frames, we might be stuck
                if (posDelta < 20)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
