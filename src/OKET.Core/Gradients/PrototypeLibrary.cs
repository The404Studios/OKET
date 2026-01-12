namespace OKET.Core.Gradients;

/// <summary>
/// Prototype Library: Recognition through similarity, naming through stability.
///
/// CORE PRINCIPLE: Never act based on a "name."
/// Act based on a recognized gradient pattern and its historical outcomes.
/// Names are just metadata AFTER stability.
///
/// Recognition flow:
/// 1. New gradient object appears
/// 2. Compute signature vector
/// 3. Find nearest prototypes
/// 4. If close enough → treat as "mapped before"
/// 5. If not → create provisional prototype (UnknownProto#N)
/// 6. Track stability over time
/// 7. Only name after passing stability gates
///
/// Stability gates:
/// - Persists across N frames
/// - Signature drift stays under threshold
/// - Survives viewpoint shifts
/// - Action-outcome consistency reaches threshold
/// </summary>
public sealed class PrototypeLibrary
{
    private readonly List<Prototype> _prototypes = new();
    private readonly Dictionary<string, int> _namedPrototypes = new(); // name → prototype ID

    // Configuration
    private readonly float _matchThreshold;
    private readonly int _stabilityFramesRequired;
    private readonly float _driftThreshold;
    private readonly int _outcomeConsistencyRequired;

    private int _nextPrototypeId;

    // Statistics
    private int _totalMatches;
    private int _totalNovelCreations;
    private int _totalNamings;

    public int PrototypeCount => _prototypes.Count;
    public int NamedCount => _namedPrototypes.Count;
    public int TotalMatches => _totalMatches;
    public int TotalNovelCreations => _totalNovelCreations;

    public PrototypeLibrary(
        float matchThreshold = 0.6f,
        int stabilityFramesRequired = 60,
        float driftThreshold = 0.15f,
        int outcomeConsistencyRequired = 5)
    {
        _matchThreshold = matchThreshold;
        _stabilityFramesRequired = stabilityFramesRequired;
        _driftThreshold = driftThreshold;
        _outcomeConsistencyRequired = outcomeConsistencyRequired;
    }

    /// <summary>
    /// Match a signature to known prototypes.
    /// Returns the best match or creates a new provisional prototype.
    /// </summary>
    public PrototypeMatch Match(SignatureVector signature, long frameId)
    {
        // Find best matching prototype
        Prototype? bestMatch = null;
        float bestSimilarity = 0;

        foreach (var proto in _prototypes)
        {
            float similarity = proto.Centroid.SimilarityTo(signature);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                bestMatch = proto;
            }
        }

        // If good match found
        if (bestMatch != null && bestSimilarity >= _matchThreshold)
        {
            _totalMatches++;
            bestMatch.RecordMatch(signature, frameId);

            return new PrototypeMatch
            {
                PrototypeId = bestMatch.Id,
                Similarity = bestSimilarity,
                Confidence = bestMatch.Confidence,
                IsKnown = true,
                IsStable = bestMatch.IsStable,
                Name = bestMatch.StableName,
                Behavior = bestMatch.Behavior
            };
        }

        // No good match - create provisional prototype
        var newProto = CreateProvisionalPrototype(signature, frameId);
        _totalNovelCreations++;

        return new PrototypeMatch
        {
            PrototypeId = newProto.Id,
            Similarity = 1f, // Perfect match to itself
            Confidence = 0.3f, // Low confidence (provisional)
            IsKnown = false,
            IsStable = false,
            Name = null,
            Behavior = new TokenBehavior()
        };
    }

    /// <summary>
    /// Create a provisional (unnamed) prototype.
    /// </summary>
    private Prototype CreateProvisionalPrototype(SignatureVector signature, long frameId)
    {
        var proto = new Prototype(_nextPrototypeId++, signature, frameId);
        _prototypes.Add(proto);
        return proto;
    }

    /// <summary>
    /// Record outcome for a prototype (for learning behavior).
    /// </summary>
    public void RecordOutcome(int prototypeId, ActionType action, TransitionOutcome outcome)
    {
        var proto = _prototypes.FirstOrDefault(p => p.Id == prototypeId);
        proto?.RecordOutcome(action, outcome);
    }

    /// <summary>
    /// Try to stabilize and name a prototype.
    /// Returns true if naming succeeded.
    /// </summary>
    public bool TryStabilizeAndName(int prototypeId, string proposedName)
    {
        var proto = _prototypes.FirstOrDefault(p => p.Id == prototypeId);
        if (proto == null) return false;

        // Check stability gates
        if (!CheckStabilityGates(proto))
            return false;

        // Assign name
        proto.AssignStableName(proposedName);
        _namedPrototypes[proposedName] = prototypeId;
        _totalNamings++;

        return true;
    }

    /// <summary>
    /// Check if prototype passes all stability gates.
    /// </summary>
    private bool CheckStabilityGates(Prototype proto)
    {
        // Gate 1: Persists across N frames
        if (proto.AgeFrames < _stabilityFramesRequired)
            return false;

        // Gate 2: Signature drift under threshold
        if (proto.AverageDrift > _driftThreshold)
            return false;

        // Gate 3: Action-outcome consistency
        if (proto.OutcomeConsistency < _outcomeConsistencyRequired)
            return false;

        // Gate 4: Confidence threshold
        if (proto.Confidence < 0.6f)
            return false;

        return true;
    }

    /// <summary>
    /// Auto-name prototypes that have become stable.
    /// </summary>
    public int AutoNameStablePrototypes()
    {
        int named = 0;

        foreach (var proto in _prototypes.Where(p => p.StableName == null))
        {
            if (CheckStabilityGates(proto))
            {
                // Generate name based on characteristics
                string name = GenerateAutoName(proto);
                proto.AssignStableName(name);
                _namedPrototypes[name] = proto.Id;
                _totalNamings++;
                named++;
            }
        }

        return named;
    }

    /// <summary>
    /// Generate automatic name based on prototype characteristics.
    /// </summary>
    private static string GenerateAutoName(Prototype proto)
    {
        var sig = proto.Centroid;

        // Determine primary characteristic
        string motion = sig.Speed > 0.3f ? "Moving" : "Static";
        string size = sig.Area switch
        {
            < 10 => "Small",
            < 50 => "Medium",
            _ => "Large"
        };
        string color = sig.Saturation > 0.5f
            ? GetColorName(sig.DominantHue)
            : "Gray";

        string behavior = proto.Behavior.DamageTendency > 0.5f ? "Threat" :
                         proto.Behavior.BenefitTendency > 0.5f ? "Resource" :
                         proto.Behavior.ObstacleTendency > 0.5f ? "Obstacle" :
                         "Entity";

        return $"{motion}{size}{color}{behavior}_{proto.Id}";
    }

    private static string GetColorName(float hue)
    {
        return hue switch
        {
            < 0.05f or > 0.95f => "Red",
            < 0.15f => "Orange",
            < 0.2f => "Yellow",
            < 0.45f => "Green",
            < 0.55f => "Cyan",
            < 0.7f => "Blue",
            < 0.85f => "Purple",
            _ => "Pink"
        };
    }

    /// <summary>
    /// Get prototype by ID.
    /// </summary>
    public Prototype? GetPrototype(int id)
    {
        return _prototypes.FirstOrDefault(p => p.Id == id);
    }

    /// <summary>
    /// Get prototype by name.
    /// </summary>
    public Prototype? GetPrototypeByName(string name)
    {
        if (_namedPrototypes.TryGetValue(name, out int id))
            return GetPrototype(id);
        return null;
    }

    /// <summary>
    /// Update all prototypes (decay, merge similar, etc.).
    /// </summary>
    public void Update(long frameId)
    {
        foreach (var proto in _prototypes)
        {
            proto.Update(frameId);
        }

        // Merge very similar prototypes
        MergeSimilarPrototypes();

        // Remove dead prototypes
        _prototypes.RemoveAll(p => p.IsDead);
    }

    private void MergeSimilarPrototypes()
    {
        for (int i = 0; i < _prototypes.Count; i++)
        {
            for (int j = i + 1; j < _prototypes.Count; j++)
            {
                var a = _prototypes[i];
                var b = _prototypes[j];

                // Don't merge if either is named
                if (a.StableName != null || b.StableName != null)
                    continue;

                // Check similarity
                float similarity = a.Centroid.SimilarityTo(b.Centroid);
                if (similarity > 0.9f)
                {
                    // Merge b into a
                    a.MergeFrom(b);
                    b.MarkDead();
                }
            }
        }
    }

    /// <summary>
    /// Get diagnostics.
    /// </summary>
    public string GetDiagnostics()
    {
        int stable = _prototypes.Count(p => p.IsStable);
        int provisional = _prototypes.Count(p => !p.IsStable);

        return $"""
            === PROTOTYPE LIBRARY ===
            Total: {_prototypes.Count} (stable={stable}, provisional={provisional})
            Named: {_namedPrototypes.Count}
            Matches: {_totalMatches}, Novel: {_totalNovelCreations}

            Top Prototypes:
            {string.Join("\n", _prototypes.OrderByDescending(p => p.MatchCount).Take(5).Select(p => $"  {p}"))}
            =========================
            """;
    }
}

/// <summary>
/// A prototype pattern in the library.
/// </summary>
public sealed class Prototype
{
    private readonly int _id;
    private SignatureVector _centroid;
    private SignatureVector _variance; // Spread around centroid
    private readonly long _createdFrame;
    private long _lastMatchFrame;

    // Stability tracking
    private readonly Queue<float> _driftHistory = new();
    private float _averageDrift;
    private int _matchCount;
    private int _ageFrames;
    private float _confidence;

    // Outcome tracking
    private readonly Dictionary<ActionType, ActionOutcomes> _actionOutcomes = new();
    private TokenBehavior _behavior;
    private int _outcomeConsistency;

    // Identity
    private string? _stableName;
    private bool _isDead;

    public int Id => _id;
    public SignatureVector Centroid => _centroid;
    public int AgeFrames => _ageFrames;
    public int MatchCount => _matchCount;
    public float AverageDrift => _averageDrift;
    public float Confidence => _confidence;
    public int OutcomeConsistency => _outcomeConsistency;
    public bool IsStable => _stableName != null;
    public string? StableName => _stableName;
    public TokenBehavior Behavior => _behavior;
    public bool IsDead => _isDead;

    public Prototype(int id, SignatureVector initial, long frameId)
    {
        _id = id;
        _centroid = initial;
        _createdFrame = frameId;
        _lastMatchFrame = frameId;
        _confidence = 0.3f;
    }

    /// <summary>
    /// Record a match and update centroid.
    /// </summary>
    public void RecordMatch(SignatureVector signature, long frameId)
    {
        _matchCount++;
        _lastMatchFrame = frameId;

        // Compute drift
        float drift = _centroid.DistanceTo(signature);
        _driftHistory.Enqueue(drift);
        while (_driftHistory.Count > 30)
            _driftHistory.Dequeue();
        _averageDrift = _driftHistory.Average();

        // Update centroid (exponential moving average)
        float learningRate = 0.1f / (1f + _matchCount * 0.01f); // Slower as we learn more
        UpdateCentroid(signature, learningRate);

        // Update confidence
        _confidence = Math.Min(1f, _matchCount / 20f) * (1f - _averageDrift);
    }

    private void UpdateCentroid(SignatureVector signature, float rate)
    {
        var centArray = _centroid.ToArray();
        var sigArray = signature.ToArray();

        for (int i = 0; i < centArray.Length; i++)
        {
            centArray[i] = centArray[i] * (1f - rate) + sigArray[i] * rate;
        }

        // Reconstruct centroid from array
        _centroid = new SignatureVector
        {
            VelocityX = centArray[0],
            VelocityY = centArray[1],
            Speed = centArray[2],
            Acceleration = centArray[3],
            Area = centArray[4] * 100f,
            AspectRatio = centArray[5],
            Compactness = centArray[6],
            EdgeDensity = centArray[7],
            DominantHue = centArray[8],
            HueVariance = centArray[9],
            Saturation = centArray[10],
            Value = centArray[11],
            AgeFrames = (int)(centArray[12] * 100f),
            Jitter = centArray[13],
            Stability = centArray[14],
            NormalizedX = centArray[15],
            NormalizedY = centArray[16],
            Confidence = centArray[17]
        };
    }

    /// <summary>
    /// Record outcome for an action.
    /// </summary>
    public void RecordOutcome(ActionType action, TransitionOutcome outcome)
    {
        if (!_actionOutcomes.TryGetValue(action, out var outcomes))
        {
            outcomes = new ActionOutcomes();
            _actionOutcomes[action] = outcomes;
        }

        outcomes.Add(outcome);

        // Update behavior
        UpdateBehavior(outcome);

        // Check consistency
        _outcomeConsistency = _actionOutcomes.Values.Sum(o => o.Count >= 3 ? 1 : 0);
    }

    private void UpdateBehavior(TransitionOutcome outcome)
    {
        _behavior = new TokenBehavior
        {
            ApproachTendency = _behavior.ApproachTendency * 0.95f +
                (outcome.Risk > 0.5f ? 0.3f : 0f) * 0.05f,
            DamageTendency = _behavior.DamageTendency * 0.95f +
                (outcome.Success < -0.3f ? 0.5f : 0f) * 0.05f,
            BenefitTendency = _behavior.BenefitTendency * 0.95f +
                (outcome.Success > 0.3f ? 0.5f : 0f) * 0.05f,
            ObstacleTendency = _behavior.ObstacleTendency,
            Predictability = _behavior.Predictability * 0.95f +
                (outcome.InfoGain < 0.2f ? 0.3f : 0f) * 0.05f,
            EncounterCount = _behavior.EncounterCount + 1
        };
    }

    /// <summary>
    /// Assign stable name (only after stability gates pass).
    /// </summary>
    public void AssignStableName(string name)
    {
        _stableName = name;
    }

    /// <summary>
    /// Update age and decay unused prototypes.
    /// </summary>
    public void Update(long frameId)
    {
        _ageFrames = (int)(frameId - _createdFrame);

        // Decay confidence if not matched recently
        int framesSinceMatch = (int)(frameId - _lastMatchFrame);
        if (framesSinceMatch > 300 && _stableName == null)
        {
            _confidence *= 0.99f;
            if (_confidence < 0.1f)
                _isDead = true;
        }
    }

    /// <summary>
    /// Merge another prototype into this one.
    /// </summary>
    public void MergeFrom(Prototype other)
    {
        float totalMatches = _matchCount + other._matchCount;
        float w1 = _matchCount / totalMatches;
        float w2 = other._matchCount / totalMatches;

        // Weighted centroid
        var c1 = _centroid.ToArray();
        var c2 = other._centroid.ToArray();
        for (int i = 0; i < c1.Length; i++)
        {
            c1[i] = c1[i] * w1 + c2[i] * w2;
        }
        UpdateCentroid(other._centroid, w2);

        _matchCount += other._matchCount;
        _behavior = _behavior.MergeWith(other._behavior);
    }

    /// <summary>
    /// Mark as dead (will be removed).
    /// </summary>
    public void MarkDead() => _isDead = true;

    public override string ToString()
    {
        string name = _stableName ?? $"Proto#{_id}";
        return $"{name}: matches={_matchCount} drift={_averageDrift:F3} conf={_confidence:F2}";
    }
}

/// <summary>
/// Result of prototype matching.
/// </summary>
public readonly struct PrototypeMatch
{
    public int PrototypeId { get; init; }
    public float Similarity { get; init; }
    public float Confidence { get; init; }
    public bool IsKnown { get; init; }
    public bool IsStable { get; init; }
    public string? Name { get; init; }
    public TokenBehavior Behavior { get; init; }
}

/// <summary>
/// Action outcomes for a prototype.
/// </summary>
internal sealed class ActionOutcomes
{
    private readonly List<TransitionOutcome> _outcomes = new();

    public int Count => _outcomes.Count;
    public float AverageSuccess => _outcomes.Count > 0 ? _outcomes.Average(o => o.Success) : 0;

    public void Add(TransitionOutcome outcome)
    {
        _outcomes.Add(outcome);
        if (_outcomes.Count > 100)
            _outcomes.RemoveAt(0);
    }
}
