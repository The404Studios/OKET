namespace OKET.Core.Memory;

using OKET.Core.Operators;

/// <summary>
/// A rolling graph of references for real-time working memory.
///
/// This is "what thinking can currently see and remember."
///
/// References in the graph:
/// - Are time-windowed (older refs expire from working memory)
/// - Can be queried by type, tag, time, bind state
/// - Form chains: Detection → Track → Commitment → Action → Outcome
///
/// The graph is the substrate for "operational understanding" -
/// thinking can literally say: "I heard X, saw Y, they agreed, I committed, it worked."
/// </summary>
public sealed class ReferenceGraph
{
    private readonly Dictionary<RefId, ReferenceNode> _nodes = new();
    private readonly object _lock = new();

    /// <summary>
    /// How long references stay in working memory (default 60 seconds).
    /// </summary>
    public TimeSpan WorkingMemoryWindow { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum nodes in working memory.
    /// </summary>
    public int MaxNodes { get; set; } = 1000;

    /// <summary>
    /// Number of nodes currently in the graph.
    /// </summary>
    public int Count
    {
        get { lock (_lock) return _nodes.Count; }
    }

    /// <summary>
    /// Add a reference to the graph.
    /// </summary>
    public void Add(ReferenceNode node)
    {
        lock (_lock)
        {
            _nodes[node.Id] = node;
            PruneIfNeeded();
        }
    }

    /// <summary>
    /// Get a reference by ID.
    /// </summary>
    public ReferenceNode? Get(RefId id)
    {
        lock (_lock)
        {
            return _nodes.TryGetValue(id, out var node) ? node : null;
        }
    }

    /// <summary>
    /// Get all references of a specific type.
    /// </summary>
    public IReadOnlyList<ReferenceNode> GetByType(RefType type)
    {
        lock (_lock)
        {
            return _nodes.Values.Where(n => n.Type == type).ToList();
        }
    }

    /// <summary>
    /// Get all references with a specific tag.
    /// </summary>
    public IReadOnlyList<ReferenceNode> GetByTag(string tag)
    {
        lock (_lock)
        {
            return _nodes.Values.Where(n => n.Tags.Contains(tag)).ToList();
        }
    }

    /// <summary>
    /// Get all references with a specific bind state.
    /// </summary>
    public IReadOnlyList<ReferenceNode> GetByBindState(BindState state)
    {
        lock (_lock)
        {
            return _nodes.Values.Where(n => n.Bind == state).ToList();
        }
    }

    /// <summary>
    /// Get references within a time window.
    /// </summary>
    public IReadOnlyList<ReferenceNode> GetRecent(TimeSpan window)
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - window;
            return _nodes.Values.Where(n => n.TimeCreated >= cutoff).ToList();
        }
    }

    /// <summary>
    /// Get the chain of references from a starting node, following links.
    /// e.g., Detection → Track → Commitment → Action → Outcome
    /// </summary>
    public IReadOnlyList<ReferenceNode> GetChain(RefId start, int maxDepth = 10)
    {
        lock (_lock)
        {
            var result = new List<ReferenceNode>();
            var visited = new HashSet<RefId>();
            var queue = new Queue<(RefId Id, int Depth)>();
            queue.Enqueue((start, 0));

            while (queue.Count > 0)
            {
                var (id, depth) = queue.Dequeue();
                if (depth > maxDepth || visited.Contains(id))
                    continue;

                visited.Add(id);
                if (_nodes.TryGetValue(id, out var node))
                {
                    result.Add(node);
                    foreach (var link in node.Links)
                        queue.Enqueue((link, depth + 1));
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Get all inherited references (global structure).
    /// </summary>
    public IReadOnlyList<ReferenceNode> GetInherited()
    {
        lock (_lock)
        {
            return _nodes.Values.Where(n => n.Bind == BindState.Inherited).ToList();
        }
    }

    /// <summary>
    /// Query references with multiple criteria.
    /// </summary>
    public IReadOnlyList<ReferenceNode> Query(
        RefType? type = null,
        BindState? bindState = null,
        string? tag = null,
        TimeSpan? maxAge = null,
        float? minValidity = null,
        int limit = 100)
    {
        lock (_lock)
        {
            var cutoff = maxAge.HasValue ? DateTime.UtcNow - maxAge.Value : DateTime.MinValue;

            return _nodes.Values
                .Where(n => !type.HasValue || n.Type == type.Value)
                .Where(n => !bindState.HasValue || n.Bind == bindState.Value)
                .Where(n => tag == null || n.Tags.Contains(tag))
                .Where(n => n.TimeCreated >= cutoff)
                .Where(n => !minValidity.HasValue || n.Validity >= minValidity.Value)
                .OrderByDescending(n => n.TimeCreated)
                .Take(limit)
                .ToList();
        }
    }

    /// <summary>
    /// Update validity for all references based on current cognitive state.
    /// </summary>
    public void UpdateValidities(float strainDelta, float outcomeDelta)
    {
        lock (_lock)
        {
            bool survived = outcomeDelta >= 0 && strainDelta <= 0;
            float validityDelta = Math.Abs(strainDelta) + Math.Abs(outcomeDelta);

            // Only update recent references (they're what led to current outcome)
            var recent = _nodes.Values
                .Where(n => n.AgeMs < 1000) // Last second
                .Where(n => n.Bind != BindState.Absent);

            foreach (var node in recent)
            {
                node.RecordValidation(survived, validityDelta);
            }
        }
    }

    /// <summary>
    /// Prune old references from working memory.
    /// </summary>
    private void PruneIfNeeded()
    {
        // Prune by time
        var cutoff = DateTime.UtcNow - WorkingMemoryWindow;
        var toRemove = _nodes.Values
            .Where(n => n.TimeCreated < cutoff && n.Bind != BindState.Inherited)
            .Select(n => n.Id)
            .ToList();

        foreach (var id in toRemove)
            _nodes.Remove(id);

        // Prune by count if still too many
        if (_nodes.Count > MaxNodes)
        {
            var excess = _nodes.Values
                .Where(n => n.Bind != BindState.Inherited)
                .OrderBy(n => n.Validity)
                .ThenBy(n => n.TimeCreated)
                .Take(_nodes.Count - MaxNodes)
                .Select(n => n.Id)
                .ToList();

            foreach (var id in excess)
                _nodes.Remove(id);
        }
    }

    /// <summary>
    /// Get summary statistics.
    /// </summary>
    public string GetSummary()
    {
        lock (_lock)
        {
            var byType = _nodes.Values.GroupBy(n => n.Type)
                .Select(g => $"{g.Key}:{g.Count()}");
            var byBind = _nodes.Values.GroupBy(n => n.Bind)
                .Select(g => $"{g.Key}:{g.Count()}");

            return $"ReferenceGraph: {_nodes.Count} nodes\n" +
                   $"  ByType: {string.Join(", ", byType)}\n" +
                   $"  ByBind: {string.Join(", ", byBind)}";
        }
    }
}
