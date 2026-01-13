using OKET.Core.Actions;
using OKET.Core.Navigation;
using OKET.Core.State;
using OKET.Core.Types;

namespace OKET.Agent.Decision.Skills;

/// <summary>
/// Skill for navigating to destinations and following players.
/// Uses NavMesh for pathfinding and generates movement actions.
/// </summary>
public sealed class NavigationSkill : SkillBase
{
    public override string Name => "Navigate";
    public override IReadOnlySet<StrategicMode> Modes { get; } = new HashSet<StrategicMode>
    {
        StrategicMode.Seek,
        StrategicMode.Reposition,
        StrategicMode.Support
    };

    private readonly NavigationController _navController;
    private NavigationGoal? _currentGoal;
    private int _framesSinceGoalSet;
    private const int MaxGoalFrames = 600; // 20 seconds max per goal

    // State tracking
    private Vector2 _lastPosition;
    private float _estimatedRotation;

    public NavigationController NavController => _navController;

    public NavigationSkill(NavigationController? navController = null)
    {
        _navController = navController ?? new NavigationController();
    }

    public override void Reset()
    {
        base.Reset();
        _navController.Stop();
        _currentGoal = null;
        _framesSinceGoalSet = 0;
    }

    /// <summary>
    /// Set a destination to navigate to.
    /// </summary>
    public void SetDestination(Vector2 destination, string? reason = null)
    {
        _currentGoal = new NavigationGoal
        {
            Type = NavigationGoalType.Position,
            Position = destination,
            Reason = reason ?? "Navigate to position"
        };
        _navController.SetDestination(destination);
        _framesSinceGoalSet = 0;
    }

    /// <summary>
    /// Set a player to follow.
    /// </summary>
    public void SetFollowTarget(int playerId, Vector2 playerPosition, string? playerName = null)
    {
        _currentGoal = new NavigationGoal
        {
            Type = NavigationGoalType.FollowPlayer,
            TargetId = playerId,
            Position = playerPosition,
            Reason = $"Following {playerName ?? $"Player {playerId}"}"
        };
        _navController.SetFollowTarget(playerId, playerPosition);
        _framesSinceGoalSet = 0;
    }

    /// <summary>
    /// Set a pattern-based destination (e.g., "find cover", "go to hallway").
    /// </summary>
    public void SetPatternGoal(string pattern, Vector2 currentPosition)
    {
        var nodes = _navController.NavMesh.GetNodesByPattern(pattern);
        if (nodes.Count == 0)
            return;

        // Find nearest matching node
        var nearest = nodes.OrderBy(n => Vector2.Distance(n.Position, currentPosition)).First();

        _currentGoal = new NavigationGoal
        {
            Type = NavigationGoalType.Pattern,
            Position = nearest.Position,
            Pattern = pattern,
            Reason = $"Navigate to {pattern}"
        };
        _navController.SetDestination(nearest.Position);
        _framesSinceGoalSet = 0;
    }

    public override ActionPlan Execute(GameState state, StrategicMode mode)
    {
        IsActive = true;
        _framesSinceGoalSet++;

        // Update position tracking
        var currentPos = EstimatePosition(state);

        // Check if goal timed out
        if (_framesSinceGoalSet > MaxGoalFrames)
        {
            Reset();
            return CreatePlan(state, mode, "Navigation timeout - resetting");
        }

        // Update follow target position if following
        if (_currentGoal?.Type == NavigationGoalType.FollowPlayer && _currentGoal.TargetId.HasValue)
        {
            var targetPlayer = FindPlayer(state, _currentGoal.TargetId.Value);
            if (targetPlayer != null)
            {
                _navController.UpdateFollowTarget(targetPlayer.Value);
            }
        }

        // Check if we've reached the goal
        if (!_navController.HasPath)
        {
            if (_currentGoal?.Type == NavigationGoalType.FollowPlayer)
            {
                // Keep following - recalculate path
                if (_currentGoal.TargetId.HasValue)
                {
                    var targetPlayer = FindPlayer(state, _currentGoal.TargetId.Value);
                    if (targetPlayer != null)
                    {
                        _navController.SetFollowTarget(_currentGoal.TargetId.Value, targetPlayer.Value);
                    }
                }
            }
            else
            {
                // Goal reached
                return CreatePlan(state, mode, "Destination reached");
            }
        }

        // Get movement actions from navigation controller
        var actions = _navController.GetMovementActions(currentPos, _estimatedRotation);

        // Update last position
        _lastPosition = currentPos;

        string reason = _currentGoal?.Reason ?? "Navigating";
        return CreatePlan(state, mode, reason, actions.ToArray());
    }

    /// <summary>
    /// Estimate current position from game state.
    /// </summary>
    private static Vector2 EstimatePosition(GameState state)
    {
        // Try to use player position if available
        // Otherwise use screen center as reference
        return new Vector2(state.ScreenSize.X / 2f, state.ScreenSize.Y / 2f);
    }

    /// <summary>
    /// Find a player's position by ID.
    /// </summary>
    private static Vector2? FindPlayer(GameState state, int playerId)
    {
        // Look for player in detections
        var playerDetection = state.Detections.Detections
            .FirstOrDefault(d => d.TrackId == playerId);

        if (playerDetection != null)
        {
            return playerDetection.Box.Center;
        }

        return null;
    }

    /// <summary>
    /// Learn the current environment from observations.
    /// </summary>
    public void LearnFromObservation(GameState state)
    {
        var currentPos = EstimatePosition(state);

        // Learn threat positions as hazards
        foreach (var threat in state.Detections.Threats)
        {
            _navController.LearnLocation(
                threat.Box.Center,
                NavNodeType.Hazard,
                "threat_location");
        }

        // Learn item positions as objectives
        foreach (var item in state.Detections.Items)
        {
            _navController.LearnLocation(
                item.Box.Center,
                NavNodeType.Objective,
                $"item_{item.Class}");
        }

        // Record current position as walkable
        _navController.LearnLocation(
            currentPos,
            NavNodeType.Open,
            "explored");
    }

    /// <summary>
    /// Get current navigation state for visualization.
    /// </summary>
    public NavigationState GetNavigationState()
    {
        return new NavigationState
        {
            HasPath = _navController.HasPath,
            CurrentPath = _navController.CurrentPath,
            CurrentPathIndex = _navController.CurrentPathIndex,
            CurrentGoal = _currentGoal,
            FramesSinceGoalSet = _framesSinceGoalSet
        };
    }
}

/// <summary>
/// Navigation goal definition.
/// </summary>
public sealed class NavigationGoal
{
    public NavigationGoalType Type { get; init; }
    public Vector2 Position { get; init; }
    public int? TargetId { get; init; }
    public string? Pattern { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// Types of navigation goals.
/// </summary>
public enum NavigationGoalType
{
    Position,       // Go to a specific position
    FollowPlayer,   // Follow a player
    Pattern,        // Go to a pattern-matched location
    Patrol,         // Patrol between waypoints
    Flee            // Move away from threats
}

/// <summary>
/// Current navigation state for debugging/visualization.
/// </summary>
public sealed class NavigationState
{
    public bool HasPath { get; init; }
    public List<NavNode>? CurrentPath { get; init; }
    public int CurrentPathIndex { get; init; }
    public NavigationGoal? CurrentGoal { get; init; }
    public int FramesSinceGoalSet { get; init; }
}
