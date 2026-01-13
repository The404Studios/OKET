using OKET.Core.Types;

namespace OKET.Core.Navigation;

/// <summary>
/// Navigation mesh for pathfinding and spatial awareness.
/// Represents walkable areas and connections in the environment.
/// </summary>
public sealed class NavMesh
{
    private readonly Dictionary<int, NavNode> _nodes = new();
    private readonly Dictionary<string, List<int>> _patternIndex = new();
    private readonly List<NavConnection> _connections = new();
    private int _nextNodeId;

    // Grid-based spatial index for fast lookups
    private readonly Dictionary<(int, int), List<int>> _spatialGrid = new();
    private const float GridCellSize = 100f;

    public IReadOnlyDictionary<int, NavNode> Nodes => _nodes;
    public IReadOnlyList<NavConnection> Connections => _connections;

    /// <summary>
    /// Add a navigation node at the specified position.
    /// </summary>
    public int AddNode(Vector2 position, NavNodeType type, string? pattern = null)
    {
        int id = _nextNodeId++;
        var node = new NavNode
        {
            Id = id,
            Position = position,
            Type = type,
            Pattern = pattern ?? "unknown",
            IsWalkable = true,
            LastSeen = DateTime.UtcNow
        };

        _nodes[id] = node;
        AddToSpatialGrid(id, position);

        // Index by pattern
        if (!string.IsNullOrEmpty(pattern))
        {
            if (!_patternIndex.ContainsKey(pattern))
                _patternIndex[pattern] = new List<int>();
            _patternIndex[pattern].Add(id);
        }

        return id;
    }

    /// <summary>
    /// Connect two nodes with a weighted edge.
    /// </summary>
    public void Connect(int fromId, int toId, float weight = 1f, bool bidirectional = true)
    {
        if (!_nodes.ContainsKey(fromId) || !_nodes.ContainsKey(toId))
            return;

        _connections.Add(new NavConnection
        {
            FromId = fromId,
            ToId = toId,
            Weight = weight
        });

        _nodes[fromId].Neighbors.Add(toId);

        if (bidirectional)
        {
            _connections.Add(new NavConnection
            {
                FromId = toId,
                ToId = fromId,
                Weight = weight
            });
            _nodes[toId].Neighbors.Add(fromId);
        }
    }

    /// <summary>
    /// Find path using A* algorithm.
    /// </summary>
    public List<NavNode>? FindPath(Vector2 start, Vector2 goal)
    {
        var startNode = FindNearestNode(start);
        var goalNode = FindNearestNode(goal);

        if (startNode == null || goalNode == null)
            return null;

        return FindPath(startNode.Id, goalNode.Id);
    }

    /// <summary>
    /// Find path between two node IDs using A*.
    /// </summary>
    public List<NavNode>? FindPath(int startId, int goalId)
    {
        if (!_nodes.TryGetValue(startId, out var startNode) ||
            !_nodes.TryGetValue(goalId, out var goalNode))
            return null;

        var openSet = new PriorityQueue<int, float>();
        var cameFrom = new Dictionary<int, int>();
        var gScore = new Dictionary<int, float> { [startId] = 0 };
        var fScore = new Dictionary<int, float> { [startId] = Heuristic(startNode, goalNode) };

        openSet.Enqueue(startId, fScore[startId]);

        while (openSet.Count > 0)
        {
            int current = openSet.Dequeue();

            if (current == goalId)
                return ReconstructPath(cameFrom, current);

            if (!_nodes.TryGetValue(current, out var currentNode))
                continue;

            foreach (int neighborId in currentNode.Neighbors)
            {
                if (!_nodes.TryGetValue(neighborId, out var neighbor) || !neighbor.IsWalkable)
                    continue;

                float tentativeG = gScore.GetValueOrDefault(current, float.MaxValue) +
                                   Distance(currentNode, neighbor);

                if (tentativeG < gScore.GetValueOrDefault(neighborId, float.MaxValue))
                {
                    cameFrom[neighborId] = current;
                    gScore[neighborId] = tentativeG;
                    fScore[neighborId] = tentativeG + Heuristic(neighbor, goalNode);

                    // PriorityQueue allows duplicates, which is fine for A*
                    openSet.Enqueue(neighborId, fScore[neighborId]);
                }
            }
        }

        return null; // No path found
    }

    /// <summary>
    /// Find the nearest walkable node to a position.
    /// </summary>
    public NavNode? FindNearestNode(Vector2 position)
    {
        var (gx, gy) = GetGridCell(position);

        // Search in expanding rings around the grid cell
        for (int radius = 0; radius < 10; radius++)
        {
            NavNode? nearest = null;
            float nearestDist = float.MaxValue;

            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        continue; // Only check border cells

                    var key = (gx + dx, gy + dy);
                    if (!_spatialGrid.TryGetValue(key, out var nodeIds))
                        continue;

                    foreach (int id in nodeIds)
                    {
                        if (!_nodes.TryGetValue(id, out var node) || !node.IsWalkable)
                            continue;

                        float dist = Vector2.Distance(position, node.Position);
                        if (dist < nearestDist)
                        {
                            nearestDist = dist;
                            nearest = node;
                        }
                    }
                }
            }

            if (nearest != null)
                return nearest;
        }

        // Fallback: linear search
        return _nodes.Values
            .Where(n => n.IsWalkable)
            .OrderBy(n => Vector2.Distance(position, n.Position))
            .FirstOrDefault();
    }

    /// <summary>
    /// Get all nodes matching a pattern.
    /// </summary>
    public List<NavNode> GetNodesByPattern(string pattern)
    {
        if (!_patternIndex.TryGetValue(pattern, out var ids))
            return new List<NavNode>();

        return ids
            .Where(_nodes.ContainsKey)
            .Select(id => _nodes[id])
            .ToList();
    }

    /// <summary>
    /// Update node walkability based on obstacles.
    /// </summary>
    public void UpdateWalkability(Vector2 obstaclePos, float radius, bool isWalkable)
    {
        foreach (var node in _nodes.Values)
        {
            float dist = Vector2.Distance(node.Position, obstaclePos);
            if (dist < radius)
            {
                node.IsWalkable = isWalkable;
            }
        }
    }

    /// <summary>
    /// Tag nodes in a region with a pattern.
    /// </summary>
    public void TagRegion(Vector2 center, float radius, string pattern, NavNodeType type)
    {
        foreach (var node in _nodes.Values)
        {
            float dist = Vector2.Distance(node.Position, center);
            if (dist < radius)
            {
                node.Pattern = pattern;
                node.Type = type;

                if (!_patternIndex.ContainsKey(pattern))
                    _patternIndex[pattern] = new List<int>();
                if (!_patternIndex[pattern].Contains(node.Id))
                    _patternIndex[pattern].Add(node.Id);
            }
        }
    }

    /// <summary>
    /// Generate a grid-based navmesh for an area.
    /// </summary>
    public void GenerateGrid(Vector2 min, Vector2 max, float spacing = 50f)
    {
        var nodeGrid = new Dictionary<(int, int), int>();

        // Create nodes
        for (float x = min.X; x <= max.X; x += spacing)
        {
            for (float y = min.Y; y <= max.Y; y += spacing)
            {
                int gx = (int)((x - min.X) / spacing);
                int gy = (int)((y - min.Y) / spacing);

                int id = AddNode(new Vector2(x, y), NavNodeType.Open);
                nodeGrid[(gx, gy)] = id;
            }
        }

        // Connect adjacent nodes
        foreach (var ((gx, gy), id) in nodeGrid)
        {
            // 8-directional connections
            int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
            int[] dy = { -1, -1, -1, 0, 0, 1, 1, 1 };

            for (int i = 0; i < 8; i++)
            {
                var neighborKey = (gx + dx[i], gy + dy[i]);
                if (nodeGrid.TryGetValue(neighborKey, out int neighborId))
                {
                    float weight = (dx[i] != 0 && dy[i] != 0) ? 1.414f : 1f; // Diagonal vs cardinal
                    Connect(id, neighborId, weight, bidirectional: false);
                }
            }
        }
    }

    /// <summary>
    /// Get all walkable paths for visualization.
    /// </summary>
    public List<(Vector2 From, Vector2 To)> GetWalkableEdges()
    {
        var edges = new List<(Vector2, Vector2)>();

        foreach (var conn in _connections)
        {
            if (_nodes.TryGetValue(conn.FromId, out var from) &&
                _nodes.TryGetValue(conn.ToId, out var to) &&
                from.IsWalkable && to.IsWalkable)
            {
                edges.Add((from.Position, to.Position));
            }
        }

        return edges;
    }

    private List<NavNode> ReconstructPath(Dictionary<int, int> cameFrom, int current)
    {
        var path = new List<NavNode> { _nodes[current] };

        while (cameFrom.ContainsKey(current))
        {
            current = cameFrom[current];
            path.Insert(0, _nodes[current]);
        }

        return path;
    }

    private static float Heuristic(NavNode a, NavNode b)
    {
        return Vector2.Distance(a.Position, b.Position);
    }

    private static float Distance(NavNode a, NavNode b)
    {
        return Vector2.Distance(a.Position, b.Position);
    }

    private void AddToSpatialGrid(int nodeId, Vector2 position)
    {
        var cell = GetGridCell(position);
        if (!_spatialGrid.ContainsKey(cell))
            _spatialGrid[cell] = new List<int>();
        _spatialGrid[cell].Add(nodeId);
    }

    private static (int, int) GetGridCell(Vector2 position)
    {
        return ((int)(position.X / GridCellSize), (int)(position.Y / GridCellSize));
    }

    public void Clear()
    {
        _nodes.Clear();
        _connections.Clear();
        _patternIndex.Clear();
        _spatialGrid.Clear();
        _nextNodeId = 0;
    }
}

/// <summary>
/// A node in the navigation mesh.
/// </summary>
public sealed class NavNode
{
    public int Id { get; init; }
    public Vector2 Position { get; init; }
    public NavNodeType Type { get; set; }
    public string Pattern { get; set; } = "unknown";
    public bool IsWalkable { get; set; } = true;
    public DateTime LastSeen { get; set; }
    public List<int> Neighbors { get; } = new();

    // For player-related nodes
    public int? AssociatedPlayerId { get; set; }
}

/// <summary>
/// Connection between navigation nodes.
/// </summary>
public readonly struct NavConnection
{
    public int FromId { get; init; }
    public int ToId { get; init; }
    public float Weight { get; init; }
}

/// <summary>
/// Types of navigation nodes.
/// </summary>
public enum NavNodeType
{
    Open,           // Open walkable area
    Hallway,        // Narrow corridor
    Room,           // Inside a room
    Doorway,        // Door or entrance
    Cover,          // Behind cover
    HighGround,     // Elevated position
    Chokepoint,     // Narrow tactical point
    SpawnPoint,     // Player/enemy spawn
    Objective,      // Objective location
    Hazard,         // Dangerous area
    PlayerPosition  // Where a player is/was
}
