using OKET.Core.Actions;
using OKET.Core.Types;

namespace OKET.Core.Navigation;

/// <summary>
/// Controls navigation and pathfinding for the agent.
/// Converts high-level movement goals into actionable movement commands.
/// </summary>
public sealed class NavigationController
{
    private readonly NavMesh _navMesh;
    private List<NavNode>? _currentPath;
    private int _currentPathIndex;
    private Vector2? _targetPosition;
    private int? _followTargetId;

    // Movement parameters
    private const float WaypointReachedDistance = 30f;
    private const float StuckThreshold = 5f;
    private const int StuckFrameLimit = 60;

    // State tracking
    private Vector2 _lastPosition;
    private int _stuckFrames;
    private DateTime _pathStartTime;

    // Statistics
    public int PathsCompleted { get; private set; }
    public int PathsFailed { get; private set; }
    public float AveragePathTime { get; private set; }

    public NavMesh NavMesh => _navMesh;
    public bool HasPath => _currentPath != null && _currentPathIndex < _currentPath.Count;
    public Vector2? CurrentTarget => _targetPosition;
    public List<NavNode>? CurrentPath => _currentPath;
    public int CurrentPathIndex => _currentPathIndex;

    public NavigationController(NavMesh? navMesh = null)
    {
        _navMesh = navMesh ?? new NavMesh();
    }

    /// <summary>
    /// Set a destination to navigate to.
    /// </summary>
    public bool SetDestination(Vector2 destination)
    {
        _targetPosition = destination;
        _followTargetId = null;
        return RecalculatePath();
    }

    /// <summary>
    /// Set a target to follow (player, etc).
    /// </summary>
    public bool SetFollowTarget(int targetId, Vector2 targetPosition)
    {
        _followTargetId = targetId;
        _targetPosition = targetPosition;
        return RecalculatePath();
    }

    /// <summary>
    /// Update follow target position.
    /// </summary>
    public void UpdateFollowTarget(Vector2 newPosition)
    {
        if (_followTargetId.HasValue)
        {
            _targetPosition = newPosition;

            // Recalculate path if target moved significantly
            if (_currentPath != null && _currentPath.Count > 0)
            {
                var pathEnd = _currentPath[^1].Position;
                if (Vector2.Distance(pathEnd, newPosition) > 100f)
                {
                    RecalculatePath();
                }
            }
        }
    }

    /// <summary>
    /// Get movement actions to execute this frame.
    /// </summary>
    public List<GameAction> GetMovementActions(Vector2 currentPosition, float currentRotation)
    {
        var actions = new List<GameAction>();

        if (!HasPath || _targetPosition == null)
        {
            return actions;
        }

        // Check if stuck
        float movement = Vector2.Distance(currentPosition, _lastPosition);
        if (movement < StuckThreshold)
        {
            _stuckFrames++;
            if (_stuckFrames > StuckFrameLimit)
            {
                // Try to unstick
                actions.AddRange(GetUnstickActions());
                _stuckFrames = 0;
                return actions;
            }
        }
        else
        {
            _stuckFrames = 0;
        }
        _lastPosition = currentPosition;

        // Get current waypoint
        var waypoint = _currentPath![_currentPathIndex];

        // Check if reached waypoint
        float distToWaypoint = Vector2.Distance(currentPosition, waypoint.Position);
        if (distToWaypoint < WaypointReachedDistance)
        {
            _currentPathIndex++;
            if (_currentPathIndex >= _currentPath.Count)
            {
                // Path complete
                OnPathCompleted();
                return actions;
            }
            waypoint = _currentPath[_currentPathIndex];
        }

        // Calculate movement direction
        var direction = waypoint.Position - currentPosition;
        float angleToTarget = MathF.Atan2(direction.Y, direction.X);
        float angleDiff = NormalizeAngle(angleToTarget - currentRotation);

        // Determine movement keys based on angle
        actions.AddRange(GetDirectionalMovement(angleDiff, distToWaypoint));

        return actions;
    }

    /// <summary>
    /// Get directional movement based on angle to target.
    /// </summary>
    private static List<GameAction> GetDirectionalMovement(float angleDiff, float distance)
    {
        var actions = new List<GameAction>();

        // Convert angle to movement direction
        // Angle 0 = facing target, positive = target is to the right
        float absAngle = MathF.Abs(angleDiff);

        // Forward/backward
        if (absAngle < MathF.PI / 4) // Within 45 degrees
        {
            actions.Add(GameAction.Press(ActionType.MoveForward));
        }
        else if (absAngle > 3 * MathF.PI / 4) // More than 135 degrees
        {
            actions.Add(GameAction.Press(ActionType.MoveBackward));
        }

        // Strafe left/right
        if (angleDiff > MathF.PI / 8 && angleDiff < 7 * MathF.PI / 8)
        {
            actions.Add(GameAction.Press(ActionType.MoveRight));
        }
        else if (angleDiff < -MathF.PI / 8 && angleDiff > -7 * MathF.PI / 8)
        {
            actions.Add(GameAction.Press(ActionType.MoveLeft));
        }

        // Sprint if far from target
        if (distance > 200f && absAngle < MathF.PI / 3)
        {
            actions.Add(GameAction.Press(ActionType.Sprint));
        }

        return actions;
    }

    /// <summary>
    /// Get actions to try to unstick.
    /// </summary>
    private static List<GameAction> GetUnstickActions()
    {
        var actions = new List<GameAction>();

        // Random unstick movement
        var random = new Random();
        int choice = random.Next(4);

        switch (choice)
        {
            case 0:
                actions.Add(GameAction.Press(ActionType.MoveLeft));
                actions.Add(GameAction.Press(ActionType.Jump));
                break;
            case 1:
                actions.Add(GameAction.Press(ActionType.MoveRight));
                actions.Add(GameAction.Press(ActionType.Jump));
                break;
            case 2:
                actions.Add(GameAction.Press(ActionType.MoveBackward));
                break;
            case 3:
                actions.Add(GameAction.Press(ActionType.Jump));
                actions.Add(GameAction.Press(ActionType.MoveForward));
                break;
        }

        return actions;
    }

    /// <summary>
    /// Stop navigation and clear path.
    /// </summary>
    public void Stop()
    {
        _currentPath = null;
        _currentPathIndex = 0;
        _targetPosition = null;
        _followTargetId = null;
    }

    /// <summary>
    /// Recalculate path to current target.
    /// </summary>
    private bool RecalculatePath()
    {
        if (_targetPosition == null)
            return false;

        var nearestStart = _navMesh.FindNearestNode(_lastPosition);
        if (nearestStart == null)
        {
            // No navmesh nearby - try direct movement
            _currentPath = new List<NavNode>
            {
                new NavNode
                {
                    Id = -1,
                    Position = _targetPosition.Value,
                    Type = NavNodeType.Open,
                    IsWalkable = true
                }
            };
            _currentPathIndex = 0;
            _pathStartTime = DateTime.UtcNow;
            return true;
        }

        _currentPath = _navMesh.FindPath(_lastPosition, _targetPosition.Value);
        _currentPathIndex = 0;
        _pathStartTime = DateTime.UtcNow;

        if (_currentPath == null)
        {
            PathsFailed++;
            return false;
        }

        return true;
    }

    private void OnPathCompleted()
    {
        PathsCompleted++;
        var elapsed = (DateTime.UtcNow - _pathStartTime).TotalSeconds;
        AveragePathTime = AveragePathTime * 0.9f + (float)elapsed * 0.1f;

        _currentPath = null;
        _currentPathIndex = 0;

        // If following, recalculate to new position
        if (_followTargetId.HasValue && _targetPosition.HasValue)
        {
            RecalculatePath();
        }
    }

    private static float NormalizeAngle(float angle)
    {
        while (angle > MathF.PI) angle -= 2 * MathF.PI;
        while (angle < -MathF.PI) angle += 2 * MathF.PI;
        return angle;
    }

    /// <summary>
    /// Learn a new location from observation.
    /// </summary>
    public void LearnLocation(Vector2 position, NavNodeType type, string pattern)
    {
        // Check if we already have a node near this position
        var existing = _navMesh.FindNearestNode(position);
        if (existing != null && Vector2.Distance(existing.Position, position) < 25f)
        {
            // Update existing node
            existing.Type = type;
            existing.Pattern = pattern;
            existing.LastSeen = DateTime.UtcNow;
            return;
        }

        // Add new node
        int newId = _navMesh.AddNode(position, type, pattern);

        // Connect to nearby nodes
        if (existing != null)
        {
            float dist = Vector2.Distance(existing.Position, position);
            _navMesh.Connect(newId, existing.Id, dist / 50f);
        }
    }

    /// <summary>
    /// Record a player position for following/learning.
    /// </summary>
    public void RecordPlayerPosition(int playerId, Vector2 position)
    {
        LearnLocation(position, NavNodeType.PlayerPosition, $"player_{playerId}");
    }
}
