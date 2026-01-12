namespace OKET.Core.Gradients;

/// <summary>
/// Master Gradient System - Orchestrates the entire perception → action pipeline.
///
/// ARCHITECTURE:
///
///   PERCEPTION → GRADIENT OBJECTS → TOKENS → SUPERSTATES → ACTION AUTHORIZATION → MEMORY
///                                                                    ↓
///                                                              NAMING (after stability)
///
/// The flow:
/// 1. Raw frame → GradientField (local nodes)
/// 2. Clustering → GradientObjects (coherent regions)
/// 3. Tokenization → SignatureTokens (fixed signatures)
/// 4. Scene graph → Superstate (situation)
/// 5. Authorization → Action (risk-weighted decision)
/// 6. Feedback → TransitionMemory (causal learning)
/// 7. Stabilization → PrototypeLibrary (naming after stable)
///
/// PRINCIPLE: Never act based on "name." Act based on gradient pattern + historical outcomes.
/// Names are metadata after stability, not the basis for decisions.
/// </summary>
public sealed class GradientSystem
{
    // Components
    private readonly GradientField _field;
    private readonly GradientObjectTracker _tracker;
    private readonly PrototypeLibrary _prototypes;
    private readonly TransitionMemory _memory;
    private readonly ActionAuthorizer _authorizer;

    // Current state
    private readonly List<SignatureToken> _currentTokens = new();
    private Superstate? _currentSuperstate;
    private SuperstateSignature? _previousSignature;
    private AuthorizationResult? _lastAuthorization;
    private ActionType _lastAction;

    // Frame tracking
    private long _frameId;
    private int _superstateIdCounter;

    // Statistics
    private int _totalFrames;
    private int _objectsTracked;
    private int _transitionsRecorded;
    private float _avgNovelty;

    public GradientField Field => _field;
    public PrototypeLibrary Prototypes => _prototypes;
    public TransitionMemory Memory => _memory;
    public ActionAuthorizer Authorizer => _authorizer;
    public Superstate? CurrentSuperstate => _currentSuperstate;
    public IReadOnlyList<SignatureToken> CurrentTokens => _currentTokens;
    public AuthorizationResult? LastAuthorization => _lastAuthorization;
    public int TotalFrames => _totalFrames;
    public int ObjectsTracked => _objectsTracked;
    public float AverageNovelty => _avgNovelty;

    public GradientSystem(int frameWidth, int frameHeight, int cellSize = 16)
    {
        _field = new GradientField(frameWidth, frameHeight, cellSize);
        _tracker = new GradientObjectTracker();
        _prototypes = new PrototypeLibrary();
        _memory = new TransitionMemory();
        _authorizer = new ActionAuthorizer(_prototypes, _memory);
    }

    /// <summary>
    /// Process a frame through the entire pipeline.
    /// </summary>
    public GradientCycleResult ProcessFrame(FrameData frame, float urgency = 0.5f)
    {
        _frameId++;
        _totalFrames++;

        // Store previous state for transition recording
        _previousSignature = _currentSuperstate?.Signature;

        // === STAGE 1: LOCAL LAYER - RAW FIELDS ===
        _field.Update(frame, _frameId);

        // === STAGE 2: REGIONAL LAYER - GRADIENT OBJECTS ===
        var objects = _tracker.Update(_field, _frameId);
        _objectsTracked = objects.Count;

        // === STAGE 3: TOKENIZATION ===
        _currentTokens.Clear();
        float totalNovelty = 0;

        foreach (var obj in objects)
        {
            // Get signature and match to prototypes
            var signature = obj.GetSignature();
            var match = _prototypes.Match(signature, _frameId);

            // Create/update token
            var token = new SignatureToken(obj.ObjectId, _frameId);
            token.UpdateFromObject(obj, _frameId);
            token.SetPrototypeMatch(match.PrototypeId, match.Similarity, match.Confidence);

            // Update object with prototype info
            obj.SetPrototype(match.PrototypeId, match.Similarity);
            if (match.Name != null)
                obj.AssignStableName(match.Name);

            // Update token behavior from prototype
            if (match.IsKnown)
                token.UpdateBehavior(match.Behavior);

            _currentTokens.Add(token);
            totalNovelty += token.Novelty;
        }

        _avgNovelty = _currentTokens.Count > 0 ? totalNovelty / _currentTokens.Count : 0;

        // === STAGE 4: GLOBAL LAYER - SUPERSTATE ===
        var superstate = new Superstate(_superstateIdCounter++, _frameId);
        superstate.BuildFromTokens(_currentTokens, _frameId);
        _currentSuperstate = superstate;

        // === STAGE 5: ACTION AUTHORIZATION ===
        _lastAuthorization = _authorizer.Authorize(superstate, urgency);

        // === STAGE 6: UPDATE PROTOTYPES ===
        _prototypes.Update(_frameId);

        // Auto-name stable prototypes periodically
        if (_frameId % 100 == 0)
        {
            _prototypes.AutoNameStablePrototypes();
        }

        return new GradientCycleResult
        {
            FrameId = _frameId,
            ObjectCount = objects.Count,
            TokenCount = _currentTokens.Count,
            Superstate = superstate.GetSummary(),
            Authorization = _lastAuthorization.Value,
            AverageNovelty = _avgNovelty,
            PrototypeCount = _prototypes.PrototypeCount,
            NamedPrototypes = _prototypes.NamedCount
        };
    }

    /// <summary>
    /// Record outcome of action taken.
    /// This closes the learning loop.
    /// </summary>
    public void RecordOutcome(
        ActionType actionTaken,
        float successScore,
        float riskIncurred,
        float infoGained,
        bool survived)
    {
        if (_previousSignature == null || _currentSuperstate == null)
            return;

        // Create outcome
        var outcome = new TransitionOutcome
        {
            Success = successScore,
            Risk = riskIncurred,
            InfoGain = infoGained,
            Improved = successScore > 0,
            Survived = survived
        };

        // Record transition
        _memory.RecordTransition(
            _previousSignature.Value,
            actionTaken,
            _currentSuperstate.Signature,
            outcome,
            _avgNovelty);
        _transitionsRecorded++;

        // Update prototypes with outcome
        foreach (var token in _currentTokens)
        {
            if (token.PrototypeId >= 0)
            {
                _prototypes.RecordOutcome(token.PrototypeId, actionTaken, outcome);
            }
        }

        // Update authorizer
        _authorizer.RecordFeedback(actionTaken, outcome);

        _lastAction = actionTaken;
    }

    /// <summary>
    /// Get the recommended action based on current state.
    /// </summary>
    public ActionType GetRecommendedAction()
    {
        return _lastAuthorization?.AuthorizedAction ?? ActionType.Observe;
    }

    /// <summary>
    /// Check if a specific prototype pattern is present.
    /// </summary>
    public bool IsPatternPresent(string patternName)
    {
        var proto = _prototypes.GetPrototypeByName(patternName);
        if (proto == null) return false;

        return _currentTokens.Any(t => t.PrototypeId == proto.Id && t.Confidence > 0.5f);
    }

    /// <summary>
    /// Get all current threat-like objects.
    /// </summary>
    public IEnumerable<SignatureToken> GetThreatLikeTokens()
    {
        return _currentTokens.Where(t =>
            t.Type == FieldType.TrackedTargetlike ||
            t.Behavior.DamageTendency > 0.5f);
    }

    /// <summary>
    /// Get all current opportunity-like objects.
    /// </summary>
    public IEnumerable<SignatureToken> GetOpportunityTokens()
    {
        return _currentTokens.Where(t =>
            t.Type == FieldType.StableColoredField ||
            t.Behavior.BenefitTendency > 0.5f);
    }

    /// <summary>
    /// Reset the system (on death/respawn).
    /// Note: Prototypes and memory persist.
    /// </summary>
    public void Reset()
    {
        _tracker.Reset();
        _currentTokens.Clear();
        _currentSuperstate = null;
        _previousSignature = null;
        _lastAuthorization = null;
    }

    /// <summary>
    /// Clear all learned data.
    /// </summary>
    public void ClearLearning()
    {
        Reset();
        // Note: We intentionally don't clear prototypes/memory
        // as those represent long-term learning
    }

    /// <summary>
    /// Get comprehensive diagnostics.
    /// </summary>
    public string GetDiagnostics()
    {
        var superstateInfo = _currentSuperstate != null
            ? _currentSuperstate.ToString()
            : "No superstate";

        var authInfo = _lastAuthorization.HasValue
            ? $"Action: {_lastAuthorization.Value.AuthorizedAction} " +
              $"(score={_lastAuthorization.Value.Score:F2}, conf={_lastAuthorization.Value.Confidence:F2})"
            : "No authorization";

        return $"""
            === GRADIENT SYSTEM ===
            Frame: {_frameId}, Objects: {_objectsTracked}, Tokens: {_currentTokens.Count}
            Avg Novelty: {_avgNovelty:F2}
            Transitions Recorded: {_transitionsRecorded}

            {superstateInfo}
            {authInfo}

            {_prototypes.GetDiagnostics()}
            {_memory.GetDiagnostics()}
            {_authorizer.GetDiagnostics()}
            =======================
            """;
    }
}

/// <summary>
/// Tracks gradient objects across frames.
/// </summary>
public sealed class GradientObjectTracker
{
    private readonly List<GradientObject> _objects = new();
    private int _nextObjectId;
    private const float MatchThreshold = 0.5f;

    public IReadOnlyList<GradientObject> Objects => _objects;

    /// <summary>
    /// Update tracking from new field data.
    /// </summary>
    public List<GradientObject> Update(GradientField field, long frameId)
    {
        // Find active cells and cluster them
        var clusters = ClusterActiveCells(field);

        // Match clusters to existing objects
        var newObjects = new List<GradientObject>();
        var matchedObjectIds = new HashSet<int>();

        foreach (var cluster in clusters)
        {
            // Create candidate object
            var candidate = new GradientObject(_nextObjectId++, frameId);
            foreach (var cell in cluster)
            {
                candidate.AddCell(cell.gx, cell.gy);
            }
            candidate.ComputeProperties(field, frameId);

            // Find best matching existing object
            GradientObject? bestMatch = null;
            float bestScore = 0;

            foreach (var existing in _objects.Where(o => !matchedObjectIds.Contains(o.ObjectId)))
            {
                float score = existing.MatchScore(candidate);
                if (score > bestScore && score > MatchThreshold)
                {
                    bestScore = score;
                    bestMatch = existing;
                }
            }

            if (bestMatch != null)
            {
                // Update existing object
                bestMatch.AddCell(candidate.Cells[0].gx, candidate.Cells[0].gy); // Just update
                bestMatch.ComputeProperties(field, frameId);
                bestMatch.MarkTracked();
                matchedObjectIds.Add(bestMatch.ObjectId);
                newObjects.Add(bestMatch);
            }
            else
            {
                // New object
                newObjects.Add(candidate);
            }
        }

        _objects.Clear();
        _objects.AddRange(newObjects);

        return newObjects;
    }

    /// <summary>
    /// Cluster active cells into groups.
    /// </summary>
    private static List<List<(int gx, int gy, float activity)>> ClusterActiveCells(GradientField field)
    {
        var activeCells = field.FindActiveCells(0.1f).ToList();
        var clusters = new List<List<(int gx, int gy, float activity)>>();
        var assigned = new HashSet<(int, int)>();

        foreach (var cell in activeCells.OrderByDescending(c => c.activity))
        {
            if (assigned.Contains((cell.gx, cell.gy)))
                continue;

            // Start new cluster
            var cluster = new List<(int gx, int gy, float activity)>();
            var queue = new Queue<(int gx, int gy)>();
            queue.Enqueue((cell.gx, cell.gy));

            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();
                if (assigned.Contains((cx, cy)))
                    continue;

                var cellData = activeCells.FirstOrDefault(c => c.gx == cx && c.gy == cy);
                if (cellData.activity < 0.05f)
                    continue;

                cluster.Add(cellData);
                assigned.Add((cx, cy));

                // Add neighbors
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var neighbor = (cx + dx, cy + dy);
                        if (!assigned.Contains(neighbor) &&
                            activeCells.Any(c => c.gx == neighbor.Item1 && c.gy == neighbor.Item2))
                        {
                            queue.Enqueue(neighbor);
                        }
                    }
                }
            }

            if (cluster.Count >= 2) // Minimum cluster size
            {
                clusters.Add(cluster);
            }
        }

        return clusters;
    }

    /// <summary>
    /// Reset tracker.
    /// </summary>
    public void Reset()
    {
        _objects.Clear();
    }
}

/// <summary>
/// Result of a gradient system cycle.
/// </summary>
public readonly struct GradientCycleResult
{
    public long FrameId { get; init; }
    public int ObjectCount { get; init; }
    public int TokenCount { get; init; }
    public SituationSummary Superstate { get; init; }
    public AuthorizationResult Authorization { get; init; }
    public float AverageNovelty { get; init; }
    public int PrototypeCount { get; init; }
    public int NamedPrototypes { get; init; }
}
