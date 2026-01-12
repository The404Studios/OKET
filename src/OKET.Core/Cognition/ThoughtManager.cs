using OKET.Core.Detection;

namespace OKET.Core.Cognition;

/// <summary>
/// Manages all object thoughts for the cognitive system.
///
/// PRINCIPLE: Every detected object MUST have a thought.
/// The thought manager ensures no object passes through without processing.
///
/// Key responsibilities:
/// 1. Create thoughts for new detections
/// 2. Update existing thoughts with new data
/// 3. Expire thoughts for objects no longer visible
/// 4. Track zone transitions (Safe → Transition → Force)
/// 5. Learn from outcomes to improve predictions
/// 6. Provide aggregated cognitive state
/// </summary>
public sealed class ThoughtManager
{
    // Active thoughts indexed by track ID
    private readonly Dictionary<int, ObjectThought> _thoughts = new();

    // Learned patterns for prediction (class → prediction data)
    private readonly Dictionary<DetectionClass, PredictionLearning> _learning = new();

    // Recent outcomes for learning
    private readonly Queue<ThoughtOutcome> _outcomeHistory = new();
    private const int MaxOutcomeHistory = 100;

    // Aggregated state
    private int _forcedActionCount;
    private int _transitionCount;
    private int _safeCount;
    private float _totalThreat;
    private float _totalOpportunity;

    /// <summary>All active thoughts.</summary>
    public IReadOnlyCollection<ObjectThought> ActiveThoughts => _thoughts.Values;

    /// <summary>Thoughts requiring forced action.</summary>
    public IEnumerable<ObjectThought> ForcedThoughts =>
        _thoughts.Values.Where(t => t.ActionForced);

    /// <summary>Thoughts in transition zone (need prediction).</summary>
    public IEnumerable<ObjectThought> TransitionThoughts =>
        _thoughts.Values.Where(t => t.NeedsPrediction);

    /// <summary>Safe thoughts (can observe).</summary>
    public IEnumerable<ObjectThought> SafeThoughts =>
        _thoughts.Values.Where(t => t.IsSafe);

    /// <summary>Number of thoughts requiring forced action.</summary>
    public int ForcedActionCount => _forcedActionCount;

    /// <summary>Total threat level from all thoughts.</summary>
    public float TotalThreat => _totalThreat;

    /// <summary>Total opportunity level from all thoughts.</summary>
    public float TotalOpportunity => _totalOpportunity;

    /// <summary>Whether any forced action is required.</summary>
    public bool HasForcedAction => _forcedActionCount > 0;

    /// <summary>The most urgent thought (highest threat in force zone).</summary>
    public ObjectThought? MostUrgent =>
        ForcedThoughts.OrderByDescending(t => t.Urgency).FirstOrDefault()
        ?? TransitionThoughts.OrderByDescending(t => t.Urgency).FirstOrDefault();

    /// <summary>
    /// Process new detections and update thoughts.
    /// </summary>
    public void ProcessDetections(DetectionResult detections, long frameId)
    {
        var seenIds = new HashSet<int>();

        foreach (var detection in detections.Detections)
        {
            seenIds.Add(detection.TrackId);

            if (_thoughts.TryGetValue(detection.TrackId, out var existingThought))
            {
                // Update existing thought
                existingThought.Update(detection, frameId);
            }
            else
            {
                // Create new thought
                var newThought = new ObjectThought(detection, frameId);

                // Apply learned predictions
                ApplyLearningToThought(newThought);

                _thoughts[detection.TrackId] = newThought;
            }
        }

        // Expire thoughts for objects no longer visible
        var expiredIds = _thoughts.Keys.Where(id => !seenIds.Contains(id)).ToList();
        foreach (var id in expiredIds)
        {
            var thought = _thoughts[id];

            // If thought was in force zone and disappeared, record as potential miss
            if (thought.ActionForced && thought.Age < 60) // Less than 2 seconds
            {
                RecordOutcome(thought, ThoughtAction.Ignore, -0.3f); // Slight penalty for losing track
            }

            _thoughts.Remove(id);
        }

        // Update aggregated state
        UpdateAggregatedState();
    }

    /// <summary>
    /// Apply learned prediction data to a thought.
    /// </summary>
    private void ApplyLearningToThought(ObjectThought thought)
    {
        if (_learning.TryGetValue(thought.RecognizedClass, out var learning))
        {
            thought.ApplyLearning(
                learning.EncounterCount,
                learning.EngageSuccessRate,
                learning.IgnoreSuccessRate,
                learning.PredictionConfidence);
        }
    }

    /// <summary>
    /// Record the outcome of an action taken on a thought.
    /// </summary>
    public void RecordOutcome(ObjectThought thought, ThoughtAction actionTaken, float outcome)
    {
        // Record on the thought
        thought.RecordOutcome(actionTaken, outcome);

        // Update class-level learning
        UpdateLearning(thought.RecognizedClass, actionTaken, outcome);

        // Store in history
        _outcomeHistory.Enqueue(new ThoughtOutcome
        {
            Class = thought.RecognizedClass,
            Zone = thought.Zone,
            Action = actionTaken,
            Outcome = outcome,
            TimeToAction = thought.PredictedTimeToAction
        });

        while (_outcomeHistory.Count > MaxOutcomeHistory)
            _outcomeHistory.Dequeue();
    }

    /// <summary>
    /// Record outcome by track ID.
    /// </summary>
    public void RecordOutcome(int trackId, ThoughtAction actionTaken, float outcome)
    {
        if (_thoughts.TryGetValue(trackId, out var thought))
        {
            RecordOutcome(thought, actionTaken, outcome);
        }
    }

    /// <summary>
    /// Update class-level prediction learning.
    /// </summary>
    private void UpdateLearning(DetectionClass cls, ThoughtAction action, float outcome)
    {
        if (!_learning.TryGetValue(cls, out var learning))
        {
            learning = new PredictionLearning();
            _learning[cls] = learning;
        }

        learning.EncounterCount++;

        float successValue = outcome > 0 ? 1f : (outcome < -0.5f ? 0f : 0.5f);

        if (action == ThoughtAction.Engage)
        {
            learning.EngageSuccessRate = learning.EngageSuccessRate * 0.95f + successValue * 0.05f;
            learning.EngageAttempts++;
        }
        else if (action is ThoughtAction.Observe or ThoughtAction.Ignore)
        {
            learning.IgnoreSuccessRate = learning.IgnoreSuccessRate * 0.95f + successValue * 0.05f;
            learning.IgnoreAttempts++;
        }

        // Update prediction confidence based on total experience
        learning.PredictionConfidence = Math.Min(1f, learning.EncounterCount / 50f);
    }

    /// <summary>
    /// Update aggregated state from all thoughts.
    /// </summary>
    private void UpdateAggregatedState()
    {
        _forcedActionCount = 0;
        _transitionCount = 0;
        _safeCount = 0;
        _totalThreat = 0;
        _totalOpportunity = 0;

        foreach (var thought in _thoughts.Values)
        {
            switch (thought.Zone)
            {
                case ThoughtZone.Force:
                    _forcedActionCount++;
                    break;
                case ThoughtZone.Transition:
                    _transitionCount++;
                    break;
                case ThoughtZone.Safe:
                    _safeCount++;
                    break;
            }

            _totalThreat += thought.ThreatLevel * thought.GetActionForceMultiplier();
            _totalOpportunity += thought.OpportunityLevel;
        }
    }

    /// <summary>
    /// Get the recommended action based on all thoughts.
    /// </summary>
    public (ThoughtAction action, ObjectThought? target, float confidence) GetRecommendedAction()
    {
        var mostUrgent = MostUrgent;
        if (mostUrgent == null)
            return (ThoughtAction.Observe, null, 0.5f);

        // If forced action, use that recommendation
        if (mostUrgent.ActionForced)
        {
            return (mostUrgent.RecommendedAction, mostUrgent,
                mostUrgent.PredictionReliable ? mostUrgent.PredictionConfidence : 0.7f);
        }

        // For transition zone, use prediction
        if (mostUrgent.NeedsPrediction)
        {
            var action = mostUrgent.PredictedEngageOutcome > mostUrgent.PredictedIgnoreOutcome
                ? ThoughtAction.Engage
                : ThoughtAction.Observe;
            return (action, mostUrgent, mostUrgent.PredictionConfidence);
        }

        // Safe zone - observe
        return (ThoughtAction.Observe, mostUrgent, 0.8f);
    }

    /// <summary>
    /// Get thoughts sorted by urgency.
    /// </summary>
    public IEnumerable<ObjectThought> GetByUrgency() =>
        _thoughts.Values.OrderByDescending(t => t.Urgency * t.GetActionForceMultiplier());

    /// <summary>
    /// Get thoughts in a specific zone.
    /// </summary>
    public IEnumerable<ObjectThought> GetByZone(ThoughtZone zone) =>
        _thoughts.Values.Where(t => t.Zone == zone);

    /// <summary>
    /// Check if a specific track ID is being thought about.
    /// </summary>
    public bool HasThought(int trackId) => _thoughts.ContainsKey(trackId);

    /// <summary>
    /// Get a specific thought by track ID.
    /// </summary>
    public ObjectThought? GetThought(int trackId) =>
        _thoughts.GetValueOrDefault(trackId);

    /// <summary>
    /// Reset all thoughts (e.g., on death/respawn).
    /// </summary>
    public void Reset()
    {
        _thoughts.Clear();
        UpdateAggregatedState();
    }

    /// <summary>
    /// Get diagnostic information.
    /// </summary>
    public string GetDiagnostics()
    {
        var mostUrgent = MostUrgent;

        return $"""
            === THOUGHT MANAGER ===
            Total Thoughts: {_thoughts.Count}
            Force Zone: {_forcedActionCount}, Transition: {_transitionCount}, Safe: {_safeCount}
            Total Threat: {_totalThreat:F2}, Total Opportunity: {_totalOpportunity:F2}
            Forced Action Required: {HasForcedAction}
            Most Urgent: {mostUrgent?.ToString() ?? "none"}

            Learned Classes: {_learning.Count}
            Outcome History: {_outcomeHistory.Count}
            =======================
            """;
    }
}

/// <summary>
/// Learned prediction data for a detection class.
/// </summary>
internal sealed class PredictionLearning
{
    public int EncounterCount { get; set; }
    public float EngageSuccessRate { get; set; } = 0.5f;
    public float IgnoreSuccessRate { get; set; } = 0.5f;
    public float PredictionConfidence { get; set; }
    public int EngageAttempts { get; set; }
    public int IgnoreAttempts { get; set; }
}

/// <summary>
/// Record of a thought outcome for learning.
/// </summary>
internal readonly struct ThoughtOutcome
{
    public DetectionClass Class { get; init; }
    public ThoughtZone Zone { get; init; }
    public ThoughtAction Action { get; init; }
    public float Outcome { get; init; }
    public int TimeToAction { get; init; }
}
