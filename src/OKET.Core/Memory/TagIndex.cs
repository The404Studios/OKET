namespace OKET.Core.Memory;

/// <summary>
/// Inverted index from tags to references.
///
/// Enables fast queries like:
/// - "find all references tagged HitConfirmed in last 30 seconds"
/// - "find all AVAgree moments"
/// - "show chain where BadPosture triggered forced unlock"
///
/// Tags are handles, not truth.
/// They become boundaries when they repeatedly predict sink survival.
/// </summary>
public sealed class TagIndex
{
    private readonly Dictionary<string, HashSet<RefId>> _tagToRefs = new();
    private readonly Dictionary<RefId, HashSet<string>> _refToTags = new();
    private readonly object _lock = new();

    // Track tag reliability (how often tag predicts survival)
    private readonly Dictionary<string, TagStats> _tagStats = new();

    /// <summary>
    /// Index a reference with its tags.
    /// </summary>
    public void Index(ReferenceNode node)
    {
        lock (_lock)
        {
            foreach (var tag in node.Tags)
            {
                if (!_tagToRefs.ContainsKey(tag))
                    _tagToRefs[tag] = new HashSet<RefId>();
                _tagToRefs[tag].Add(node.Id);

                if (!_tagStats.ContainsKey(tag))
                    _tagStats[tag] = new TagStats();
            }

            _refToTags[node.Id] = new HashSet<string>(node.Tags);
        }
    }

    /// <summary>
    /// Add a tag to an existing reference.
    /// </summary>
    public void AddTag(RefId id, string tag)
    {
        lock (_lock)
        {
            if (!_tagToRefs.ContainsKey(tag))
                _tagToRefs[tag] = new HashSet<RefId>();
            _tagToRefs[tag].Add(id);

            if (!_refToTags.ContainsKey(id))
                _refToTags[id] = new HashSet<string>();
            _refToTags[id].Add(tag);

            if (!_tagStats.ContainsKey(tag))
                _tagStats[tag] = new TagStats();
        }
    }

    /// <summary>
    /// Get all RefIds with a specific tag.
    /// </summary>
    public IReadOnlySet<RefId> GetByTag(string tag)
    {
        lock (_lock)
        {
            return _tagToRefs.TryGetValue(tag, out var refs)
                ? refs
                : new HashSet<RefId>();
        }
    }

    /// <summary>
    /// Get all tags for a reference.
    /// </summary>
    public IReadOnlySet<string> GetTags(RefId id)
    {
        lock (_lock)
        {
            return _refToTags.TryGetValue(id, out var tags)
                ? tags
                : new HashSet<string>();
        }
    }

    /// <summary>
    /// Get RefIds with ALL of the specified tags.
    /// </summary>
    public IReadOnlySet<RefId> GetByAllTags(params string[] tags)
    {
        lock (_lock)
        {
            if (tags.Length == 0) return new HashSet<RefId>();

            HashSet<RefId>? result = null;
            foreach (var tag in tags)
            {
                if (!_tagToRefs.TryGetValue(tag, out var refs))
                    return new HashSet<RefId>(); // No refs have this tag

                if (result == null)
                    result = new HashSet<RefId>(refs);
                else
                    result.IntersectWith(refs);
            }

            return result ?? new HashSet<RefId>();
        }
    }

    /// <summary>
    /// Get RefIds with ANY of the specified tags.
    /// </summary>
    public IReadOnlySet<RefId> GetByAnyTag(params string[] tags)
    {
        lock (_lock)
        {
            var result = new HashSet<RefId>();
            foreach (var tag in tags)
            {
                if (_tagToRefs.TryGetValue(tag, out var refs))
                    result.UnionWith(refs);
            }
            return result;
        }
    }

    /// <summary>
    /// Record a validation result for tags.
    /// This is how tags "earn" reliability by predicting survival.
    /// </summary>
    public void RecordOutcome(RefId id, bool survived)
    {
        lock (_lock)
        {
            if (!_refToTags.TryGetValue(id, out var tags)) return;

            foreach (var tag in tags)
            {
                if (_tagStats.TryGetValue(tag, out var stats))
                {
                    if (survived)
                        stats.Successes++;
                    else
                        stats.Failures++;
                }
            }
        }
    }

    /// <summary>
    /// Get tags that have become boundaries (high reliability).
    /// </summary>
    public IReadOnlyList<string> GetBoundaryTags(float minReliability = 0.8f, int minSamples = 20)
    {
        lock (_lock)
        {
            return _tagStats
                .Where(kv => kv.Value.TotalSamples >= minSamples)
                .Where(kv => kv.Value.Reliability >= minReliability)
                .Select(kv => kv.Key)
                .ToList();
        }
    }

    /// <summary>
    /// Get tag statistics.
    /// </summary>
    public IReadOnlyDictionary<string, float> GetTagReliabilities()
    {
        lock (_lock)
        {
            return _tagStats.ToDictionary(kv => kv.Key, kv => kv.Value.Reliability);
        }
    }

    /// <summary>
    /// Remove a reference from the index.
    /// </summary>
    public void Remove(RefId id)
    {
        lock (_lock)
        {
            if (_refToTags.TryGetValue(id, out var tags))
            {
                foreach (var tag in tags)
                {
                    if (_tagToRefs.TryGetValue(tag, out var refs))
                        refs.Remove(id);
                }
                _refToTags.Remove(id);
            }
        }
    }

    /// <summary>
    /// Get all known tags.
    /// </summary>
    public IReadOnlyList<string> AllTags
    {
        get { lock (_lock) return _tagToRefs.Keys.ToList(); }
    }

    /// <summary>
    /// Get summary.
    /// </summary>
    public string GetSummary()
    {
        lock (_lock)
        {
            var boundaries = GetBoundaryTags();
            var topTags = _tagStats
                .OrderByDescending(kv => kv.Value.TotalSamples)
                .Take(10)
                .Select(kv => $"{kv.Key}({kv.Value.Reliability:F2})");

            return $"TagIndex: {_tagToRefs.Count} tags, {_refToTags.Count} refs indexed\n" +
                   $"  Boundaries: [{string.Join(", ", boundaries)}]\n" +
                   $"  Top: {string.Join(", ", topTags)}";
        }
    }

    private sealed class TagStats
    {
        public int Successes;
        public int Failures;
        public int TotalSamples => Successes + Failures;
        public float Reliability => TotalSamples > 0 ? Successes / (float)TotalSamples : 0.5f;
    }
}
