using OKET.Core.Detection;
using OKET.Core.Types;

namespace OKET.Core.Cognition;

/// <summary>
/// Represents a "thought" bound to a detected object.
///
/// CORE PRINCIPLE: Every object must have a thought.
/// No detection should pass through the system without cognitive processing.
///
/// The thought represents:
/// - What the object IS (recognition)
/// - What it MEANS (interpretation)
/// - What it REQUIRES (action mandate)
/// - What we PREDICT (learned expectation)
///
/// Thoughts live in zones:
/// - SafeZone: No forced action needed, can observe/plan
/// - ForceZone: Action is mandatory, must react immediately
/// - TransitionZone: Prediction determines if action needed
/// </summary>
public sealed class ObjectThought
{
    /// <summary>The detection this thought is bound to.</summary>
    public Detection.Detection Detection { get; }

    /// <summary>Unique thought ID (matches TrackId for continuity).</summary>
    public int ThoughtId => Detection.TrackId;

    /// <summary>Frame this thought was created.</summary>
    public long CreatedFrame { get; }

    /// <summary>Last frame this thought was updated.</summary>
    public long LastUpdateFrame { get; private set; }

    /// <summary>How many frames this thought has persisted.</summary>
    public int Age => (int)(LastUpdateFrame - CreatedFrame);

    // === RECOGNITION ===
    /// <summary>What class of object this is.</summary>
    public DetectionClass RecognizedClass => Detection.Class;

    /// <summary>Recognition confidence [0, 1].</summary>
    public float RecognitionConfidence { get; private set; }

    /// <summary>Has this object been positively identified?</summary>
    public bool IsRecognized => RecognitionConfidence > 0.6f;

    // === INTERPRETATION ===
    /// <summary>Threat level this object represents [0, 1].</summary>
    public float ThreatLevel { get; private set; }

    /// <summary>Opportunity level (positive interaction potential) [0, 1].</summary>
    public float OpportunityLevel { get; private set; }

    /// <summary>Urgency of dealing with this object [0, 1].</summary>
    public float Urgency { get; private set; }

    /// <summary>Attention this object demands [0, 1].</summary>
    public float AttentionDemand { get; private set; }

    // === ZONE CLASSIFICATION ===
    /// <summary>Current zone classification.</summary>
    public ThoughtZone Zone { get; private set; }

    /// <summary>Is action forced (must react)?</summary>
    public bool ActionForced => Zone == ThoughtZone.Force;

    /// <summary>Is object in safe zone (can observe)?</summary>
    public bool IsSafe => Zone == ThoughtZone.Safe;

    /// <summary>Is prediction needed to decide?</summary>
    public bool NeedsPrediction => Zone == ThoughtZone.Transition;

    // === PREDICTION ===
    /// <summary>Predicted time until this object requires action (frames).</summary>
    public int PredictedTimeToAction { get; private set; }

    /// <summary>Predicted outcome if we ignore this object [-1, 1].</summary>
    public float PredictedIgnoreOutcome { get; private set; }

    /// <summary>Predicted outcome if we engage this object [-1, 1].</summary>
    public float PredictedEngageOutcome { get; private set; }

    /// <summary>Confidence in predictions [0, 1].</summary>
    public float PredictionConfidence { get; private set; }

    /// <summary>Recommended action based on prediction.</summary>
    public ThoughtAction RecommendedAction { get; private set; }

    // === REACTION LEARNING ===
    /// <summary>How many times we've encountered similar objects.</summary>
    public int EncounterCount { get; private set; }

    /// <summary>Historical success rate when engaging [0, 1].</summary>
    public float HistoricalEngageSuccess { get; private set; }

    /// <summary>Historical success rate when ignoring [0, 1].</summary>
    public float HistoricalIgnoreSuccess { get; private set; }

    /// <summary>Is our prediction reliable (learned from experience)?</summary>
    public bool PredictionReliable => EncounterCount >= 5 && PredictionConfidence > 0.5f;

    public ObjectThought(Detection.Detection detection, long frameId)
    {
        Detection = detection;
        CreatedFrame = frameId;
        LastUpdateFrame = frameId;
        RecognitionConfidence = detection.Confidence;

        // Initialize interpretation based on detection
        InitializeInterpretation();
    }

    private void InitializeInterpretation()
    {
        // Interpret based on class
        (ThreatLevel, OpportunityLevel) = Detection.Class switch
        {
            DetectionClass.Zombie => (0.6f, 0f),
            DetectionClass.FastZombie => (0.8f, 0f),
            DetectionClass.PoisonZombie => (0.9f, 0f),
            DetectionClass.Headcrab => (0.4f, 0f),
            DetectionClass.ZombieHead => (0.7f, 0f),
            DetectionClass.AmmoCrate => (0f, 0.6f),
            DetectionClass.WeaponCrate => (0f, 0.7f),
            DetectionClass.HealthKit => (0f, 0.8f),
            DetectionClass.Barricade => (0f, 0.3f),
            DetectionClass.BarricadeBoard => (0f, 0.2f),
            DetectionClass.Door => (0f, 0.1f),
            _ => (0.1f, 0.1f)
        };

        // Calculate urgency based on distance and threat
        float distanceFactor = Detection.EstimatedDistance.HasValue
            ? Math.Max(0, 1f - Detection.EstimatedDistance.Value / 500f)
            : 0.5f;

        Urgency = ThreatLevel * distanceFactor;
        AttentionDemand = Math.Max(ThreatLevel, OpportunityLevel) * (0.5f + distanceFactor * 0.5f);

        // Initial zone classification
        ClassifyZone();

        // Initial prediction (will be refined by learning)
        PredictedTimeToAction = EstimateTimeToAction();
        RecommendedAction = DetermineInitialAction();
    }

    private void ClassifyZone()
    {
        float distance = Detection.EstimatedDistance ?? 200f;

        // Force zone: immediate threat, close proximity
        if (ThreatLevel > 0.5f && distance < 150f)
        {
            Zone = ThoughtZone.Force;
        }
        // Safe zone: far away or low threat
        else if (distance > 400f || (ThreatLevel < 0.3f && OpportunityLevel < 0.3f))
        {
            Zone = ThoughtZone.Safe;
        }
        // Transition zone: prediction determines action
        else
        {
            Zone = ThoughtZone.Transition;
        }
    }

    private int EstimateTimeToAction()
    {
        float distance = Detection.EstimatedDistance ?? 200f;
        float speed = Detection.Velocity?.Length ?? 50f; // Default zombie speed

        // Time = distance / speed (in frames at ~30fps)
        return (int)Math.Max(1, (distance / Math.Max(1, speed)) * 30);
    }

    private ThoughtAction DetermineInitialAction()
    {
        if (Zone == ThoughtZone.Force && ThreatLevel > OpportunityLevel)
            return ThoughtAction.Engage;
        if (Zone == ThoughtZone.Force && OpportunityLevel > ThreatLevel)
            return ThoughtAction.Interact;
        if (Zone == ThoughtZone.Safe)
            return ThoughtAction.Observe;
        return ThoughtAction.Evaluate;
    }

    /// <summary>
    /// Update this thought with new detection data.
    /// </summary>
    public void Update(Detection.Detection newDetection, long frameId)
    {
        // Update tracking
        LastUpdateFrame = frameId;

        // Update recognition confidence (temporal smoothing)
        RecognitionConfidence = RecognitionConfidence * 0.8f + newDetection.Confidence * 0.2f;

        // Re-interpret based on new data
        float newDistanceFactor = newDetection.EstimatedDistance.HasValue
            ? Math.Max(0, 1f - newDetection.EstimatedDistance.Value / 500f)
            : 0.5f;

        Urgency = ThreatLevel * newDistanceFactor;
        AttentionDemand = Math.Max(ThreatLevel, OpportunityLevel) * (0.5f + newDistanceFactor * 0.5f);

        // Reclassify zone
        ClassifyZone();

        // Update predictions
        PredictedTimeToAction = EstimateTimeToAction();
    }

    /// <summary>
    /// Apply learned prediction data.
    /// </summary>
    public void ApplyLearning(
        int encounterCount,
        float engageSuccessRate,
        float ignoreSuccessRate,
        float predictionConfidence)
    {
        EncounterCount = encounterCount;
        HistoricalEngageSuccess = engageSuccessRate;
        HistoricalIgnoreSuccess = ignoreSuccessRate;
        PredictionConfidence = predictionConfidence;

        // Update predictions based on learning
        PredictedEngageOutcome = engageSuccessRate * 2f - 1f; // Map [0,1] to [-1,1]
        PredictedIgnoreOutcome = ignoreSuccessRate * 2f - 1f;

        // Update recommended action based on learned outcomes
        if (PredictionReliable)
        {
            if (Zone == ThoughtZone.Force)
            {
                // In force zone, always engage threats
                RecommendedAction = ThreatLevel > 0.3f ? ThoughtAction.Engage : ThoughtAction.Interact;
            }
            else if (Zone == ThoughtZone.Transition)
            {
                // In transition zone, use prediction to decide
                RecommendedAction = PredictedEngageOutcome > PredictedIgnoreOutcome
                    ? ThoughtAction.Engage
                    : ThoughtAction.Observe;
            }
            else
            {
                // In safe zone, observe unless high opportunity
                RecommendedAction = OpportunityLevel > 0.6f ? ThoughtAction.Approach : ThoughtAction.Observe;
            }
        }
    }

    /// <summary>
    /// Record outcome of action taken on this object.
    /// </summary>
    public void RecordOutcome(ThoughtAction actionTaken, float outcome)
    {
        EncounterCount++;

        // Update success rates with exponential moving average
        if (actionTaken == ThoughtAction.Engage)
        {
            float success = outcome > 0 ? 1f : 0f;
            HistoricalEngageSuccess = HistoricalEngageSuccess * 0.9f + success * 0.1f;
        }
        else if (actionTaken == ThoughtAction.Observe || actionTaken == ThoughtAction.Ignore)
        {
            float success = outcome >= 0 ? 1f : 0f; // Not taking damage is success
            HistoricalIgnoreSuccess = HistoricalIgnoreSuccess * 0.9f + success * 0.1f;
        }

        // Update prediction confidence based on accuracy
        float predictionError = actionTaken switch
        {
            ThoughtAction.Engage => Math.Abs(PredictedEngageOutcome - outcome),
            ThoughtAction.Observe or ThoughtAction.Ignore => Math.Abs(PredictedIgnoreOutcome - outcome),
            _ => 0.5f
        };

        PredictionConfidence = PredictionConfidence * 0.95f + (1f - predictionError) * 0.05f;
    }

    /// <summary>
    /// Get the force multiplier for action (1.0 = normal, >1.0 = forced).
    /// </summary>
    public float GetActionForceMultiplier()
    {
        return Zone switch
        {
            ThoughtZone.Force => 1.5f + Urgency,
            ThoughtZone.Transition => 1f + (PredictedTimeToAction < 30 ? 0.3f : 0f),
            ThoughtZone.Safe => 0.5f,
            _ => 1f
        };
    }

    public override string ToString()
    {
        return $"Thought[{ThoughtId}]: {RecognizedClass} zone={Zone} threat={ThreatLevel:F2} " +
               $"action={RecommendedAction} pred_conf={PredictionConfidence:F2}";
    }
}

/// <summary>
/// Zone classification for thoughts.
/// </summary>
public enum ThoughtZone
{
    /// <summary>Safe zone - no forced action, can observe.</summary>
    Safe,

    /// <summary>Transition zone - prediction determines if action needed.</summary>
    Transition,

    /// <summary>Force zone - action is mandatory, must react.</summary>
    Force
}

/// <summary>
/// Recommended action for a thought.
/// </summary>
public enum ThoughtAction
{
    /// <summary>Just observe, no action needed.</summary>
    Observe,

    /// <summary>Need more information to decide.</summary>
    Evaluate,

    /// <summary>Move toward the object.</summary>
    Approach,

    /// <summary>Engage/attack the object.</summary>
    Engage,

    /// <summary>Interact with the object (non-combat).</summary>
    Interact,

    /// <summary>Explicitly ignore this object.</summary>
    Ignore,

    /// <summary>Flee from this object.</summary>
    Flee
}
