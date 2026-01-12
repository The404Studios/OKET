using OKET.Core.Cognition;
using OKET.Core.Detection;
using OKET.Core.Operators;

namespace OKET.Core.Gradients;

/// <summary>
/// Bridge between Gradient System and Cognitive Controller.
///
/// ARCHITECTURE:
///
///   [Frame Capture]
///         ↓
///   [Gradient System] ←→ [Pipeline Gates]
///         ↓
///   [Gradient Bridge] ←→ [Thought Manager]
///         ↓
///   [Cognitive Controller]
///         ↓
///   [Action Output]
///
/// The bridge translates:
/// - Gradient objects → Detection-compatible format
/// - Superstates → Cognitive context
/// - Token behaviors → Thought predictions
/// - Authorization → Action recommendations
///
/// It also manages the training ↔ learning cycle by:
/// - Recording cognitive outcomes back to gradient system
/// - Syncing prototype learning with thought learning
/// </summary>
public sealed class GradientBridge
{
    private readonly GradientSystem _gradientSystem;
    private readonly GatePipeline _gatePipeline;

    // Mapping between gradient objects and detection IDs
    private readonly Dictionary<int, int> _objectToDetection = new();
    private readonly Dictionary<int, int> _detectionToObject = new();
    private int _nextDetectionId = 1000; // Start high to avoid conflicts

    // Sync state
    private long _lastSyncFrame;
    private float _lastOutcome;
    private ActionType _lastGradientAction;

    // Statistics
    private int _totalSyncs;
    private int _detectionsMapped;
    private float _avgMappingConfidence;

    public GradientSystem GradientSystem => _gradientSystem;
    public GatePipeline GatePipeline => _gatePipeline;
    public int TotalSyncs => _totalSyncs;

    public GradientBridge(int frameWidth, int frameHeight)
    {
        _gradientSystem = new GradientSystem(frameWidth, frameHeight);
        _gatePipeline = new GatePipeline();
    }

    /// <summary>
    /// Process frame through gradient system and convert to cognitive format.
    /// </summary>
    public GradientBridgeResult Process(
        FrameData frame,
        DetectionResult existingDetections,
        ThoughtManager thoughtManager,
        float urgency)
    {
        _totalSyncs++;

        // === STAGE 1: PROCESS THROUGH GRADIENT SYSTEM ===
        var gradientResult = _gradientSystem.ProcessFrame(frame, urgency);

        // === STAGE 2: PROCESS THROUGH PIPELINE GATES ===
        var gatedResult = ProcessThroughGates(frame, gradientResult);

        // === STAGE 3: MAP GRADIENT OBJECTS TO DETECTIONS ===
        var mappedDetections = MapToDetections(gradientResult);

        // === STAGE 4: SYNC WITH THOUGHT MANAGER ===
        SyncWithThoughts(thoughtManager, mappedDetections, gradientResult);

        // === STAGE 5: GET UNIFIED RECOMMENDATION ===
        var recommendation = GetUnifiedRecommendation(
            gradientResult.Authorization,
            thoughtManager,
            urgency);

        _lastGradientAction = gradientResult.Authorization.AuthorizedAction;

        return new GradientBridgeResult
        {
            GradientResult = gradientResult,
            GatedResult = gatedResult,
            MappedDetections = mappedDetections,
            Recommendation = recommendation,
            PipelineGain = _gatePipeline.PipelineGain,
            IsStable = _gatePipeline.IsStable
        };
    }

    /// <summary>
    /// Process through the pipeline gate system.
    /// </summary>
    private PipelineCycleResult ProcessThroughGates(FrameData frame, GradientCycleResult gradientResult)
    {
        // Build preprocessing input from gradient field
        var prepInput = new PreprocessingInput
        {
            GrayscaleStrength = 0.8f, // Would come from actual frame analysis
            ColorStrength = 0.7f,
            Quality = gradientResult.Superstate.Confidence,
            ROIValues = new Dictionary<string, float>
            {
                ["Minimap"] = 0.3f,
                ["Center"] = 0.9f,
                ["HUD"] = 0.5f
            }
        };

        // Run through pipeline
        var result = _gatePipeline.ProcessCycle(prepInput, gradientResult.Superstate.Urgency);

        return result;
    }

    /// <summary>
    /// Map gradient objects to detection-compatible format.
    /// </summary>
    private List<GradientDetection> MapToDetections(GradientCycleResult gradientResult)
    {
        var detections = new List<GradientDetection>();
        float totalConfidence = 0;

        foreach (var token in _gradientSystem.CurrentTokens)
        {
            // Get or create detection ID
            int detectionId;
            if (_objectToDetection.TryGetValue(token.TokenId, out int existingId))
            {
                detectionId = existingId;
            }
            else
            {
                detectionId = _nextDetectionId++;
                _objectToDetection[token.TokenId] = detectionId;
                _detectionToObject[detectionId] = token.TokenId;
            }

            // Map field type to detection class
            var detectionClass = MapFieldTypeToClass(token.Type, token.Behavior);

            // Create gradient-based detection
            var detection = new GradientDetection
            {
                TrackId = detectionId,
                TokenId = token.TokenId,
                Class = detectionClass,
                Confidence = token.Confidence,
                NormalizedX = token.Signature.NormalizedX,
                NormalizedY = token.Signature.NormalizedY,
                VelocityX = token.Signature.VelocityX,
                VelocityY = token.Signature.VelocityY,
                Area = token.Signature.Area,
                FieldType = token.Type,
                Behavior = token.Behavior,
                IsNovel = token.IsNovel,
                PrototypeName = token.ResolvedName
            };

            detections.Add(detection);
            totalConfidence += token.Confidence;
            _detectionsMapped++;
        }

        _avgMappingConfidence = detections.Count > 0
            ? totalConfidence / detections.Count
            : 0;

        return detections;
    }

    /// <summary>
    /// Map gradient field type to detection class.
    /// </summary>
    private static DetectionClass MapFieldTypeToClass(FieldType fieldType, TokenBehavior behavior)
    {
        // If we have learned behavior, use it
        if (behavior.EncounterCount > 5)
        {
            if (behavior.DamageTendency > 0.5f)
                return DetectionClass.Zombie; // Generic threat
            if (behavior.BenefitTendency > 0.5f)
                return DetectionClass.HealthKit; // Generic beneficial
        }

        // Otherwise map by structure
        return fieldType switch
        {
            FieldType.TrackedTargetlike => DetectionClass.Zombie,
            FieldType.MovingCoherentField => DetectionClass.Zombie,
            FieldType.StableColoredField => DetectionClass.AmmoCrate,
            FieldType.StaticUIField => DetectionClass.Unknown,
            FieldType.ContourGateway => DetectionClass.Door,
            FieldType.FlashEvent => DetectionClass.Unknown,
            _ => DetectionClass.Unknown
        };
    }

    /// <summary>
    /// Sync gradient learning with thought manager.
    /// </summary>
    private void SyncWithThoughts(
        ThoughtManager thoughtManager,
        List<GradientDetection> detections,
        GradientCycleResult gradientResult)
    {
        foreach (var detection in detections)
        {
            var thought = thoughtManager.GetThought(detection.TrackId);
            if (thought == null) continue;

            // Apply gradient-learned behavior to thought predictions
            if (detection.Behavior.EncounterCount > 3)
            {
                // Use gradient behavior to inform thought predictions
                float engageSuccess = 1f - detection.Behavior.DamageTendency;
                float ignoreSuccess = detection.Behavior.DamageTendency < 0.3f ? 0.7f : 0.3f;

                thought.ApplyLearning(
                    detection.Behavior.EncounterCount,
                    engageSuccess,
                    ignoreSuccess,
                    detection.Confidence);
            }
        }
    }

    /// <summary>
    /// Get unified action recommendation combining gradient and thought systems.
    /// </summary>
    private ActionRecommendation GetUnifiedRecommendation(
        AuthorizationResult gradientAuth,
        ThoughtManager thoughtManager,
        float urgency)
    {
        // Get thought-based recommendation
        var (thoughtAction, thoughtTarget, thoughtConf) = thoughtManager.GetRecommendedAction();

        // Combine recommendations
        // Weight by confidence and source reliability
        float gradientWeight = gradientAuth.Confidence * 0.6f;
        float thoughtWeight = thoughtConf * 0.4f;

        // Map gradient action to thought action
        var gradientAsThought = MapGradientActionToThought(gradientAuth.AuthorizedAction);

        // If both agree, high confidence
        if (gradientAsThought == thoughtAction)
        {
            return new ActionRecommendation
            {
                Action = thoughtAction,
                Confidence = Math.Min(1f, gradientAuth.Confidence + thoughtConf * 0.5f),
                Source = RecommendationSource.Combined,
                Target = thoughtTarget,
                Reasoning = $"Gradient and thought agree: {thoughtAction}"
            };
        }

        // If disagree, use urgency to decide
        if (urgency > 0.7f)
        {
            // High urgency - use thought system (more immediate)
            return new ActionRecommendation
            {
                Action = thoughtAction,
                Confidence = thoughtConf * 0.8f,
                Source = RecommendationSource.Thought,
                Target = thoughtTarget,
                Reasoning = $"High urgency, using thought: {thoughtAction}"
            };
        }

        // Normal urgency - use gradient (more learned)
        return new ActionRecommendation
        {
            Action = gradientAsThought,
            Confidence = gradientAuth.Confidence * 0.8f,
            Source = RecommendationSource.Gradient,
            Target = thoughtTarget,
            Reasoning = $"Using gradient: {gradientAuth.AuthorizedAction} ({gradientAuth.Reasoning})"
        };
    }

    /// <summary>
    /// Map gradient ActionType to thought ThoughtAction.
    /// </summary>
    private static ThoughtAction MapGradientActionToThought(ActionType gradientAction)
    {
        return gradientAction switch
        {
            ActionType.Observe => ThoughtAction.Observe,
            ActionType.Engage => ThoughtAction.Engage,
            ActionType.Approach => ThoughtAction.Approach,
            ActionType.Retreat => ThoughtAction.Flee,
            ActionType.Kite => ThoughtAction.Engage,
            ActionType.Interact => ThoughtAction.Interact,
            ActionType.Wait => ThoughtAction.Observe,
            ActionType.Explore => ThoughtAction.Approach,
            _ => ThoughtAction.Observe
        };
    }

    /// <summary>
    /// Record outcome back to gradient system.
    /// </summary>
    public void RecordOutcome(
        float successScore,
        float riskIncurred,
        float infoGained,
        bool survived)
    {
        _lastOutcome = successScore;

        // Record to gradient system
        _gradientSystem.RecordOutcome(
            _lastGradientAction,
            successScore,
            riskIncurred,
            infoGained,
            survived);

        // Backpropagate through pipeline gates
        float errorSignal = successScore < 0 ? Math.Abs(successScore) : 0;
        _gatePipeline.Backpropagate(survived ? 1f : 0f, errorSignal);
    }

    /// <summary>
    /// Get detection by gradient token ID.
    /// </summary>
    public int? GetDetectionId(int tokenId)
    {
        return _objectToDetection.TryGetValue(tokenId, out int id) ? id : null;
    }

    /// <summary>
    /// Get token ID by detection ID.
    /// </summary>
    public int? GetTokenId(int detectionId)
    {
        return _detectionToObject.TryGetValue(detectionId, out int id) ? id : null;
    }

    /// <summary>
    /// Reset bridge state (on death/respawn).
    /// </summary>
    public void Reset()
    {
        _gradientSystem.Reset();
        _objectToDetection.Clear();
        _detectionToObject.Clear();
        _lastOutcome = 0;
    }

    /// <summary>
    /// Get diagnostics.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"""
            === GRADIENT BRIDGE ===
            Syncs: {_totalSyncs}
            Detections Mapped: {_detectionsMapped}
            Avg Mapping Confidence: {_avgMappingConfidence:F2}
            Pipeline Gain: {_gatePipeline.PipelineGain:F2}
            Pipeline Stable: {_gatePipeline.IsStable}
            Last Outcome: {_lastOutcome:F2}

            {_gradientSystem.GetDiagnostics()}
            =======================
            """;
    }
}

/// <summary>
/// Detection derived from gradient object.
/// </summary>
public readonly struct GradientDetection
{
    public int TrackId { get; init; }
    public int TokenId { get; init; }
    public DetectionClass Class { get; init; }
    public float Confidence { get; init; }
    public float NormalizedX { get; init; }
    public float NormalizedY { get; init; }
    public float VelocityX { get; init; }
    public float VelocityY { get; init; }
    public float Area { get; init; }
    public FieldType FieldType { get; init; }
    public TokenBehavior Behavior { get; init; }
    public bool IsNovel { get; init; }
    public string? PrototypeName { get; init; }

    /// <summary>Convert to standard Detection for compatibility.</summary>
    public Detection.Detection ToDetection()
    {
        return new Detection.Detection
        {
            TrackId = TrackId,
            Class = Class,
            Confidence = Confidence,
            // BoundingBox would need actual pixel coordinates
            EstimatedDistance = (1f - NormalizedY) * 500f, // Rough estimate
            Velocity = new Types.Vector2(VelocityX * 100f, VelocityY * 100f)
        };
    }
}

/// <summary>
/// Result from gradient bridge processing.
/// </summary>
public readonly struct GradientBridgeResult
{
    public GradientCycleResult GradientResult { get; init; }
    public PipelineCycleResult GatedResult { get; init; }
    public List<GradientDetection> MappedDetections { get; init; }
    public ActionRecommendation Recommendation { get; init; }
    public float PipelineGain { get; init; }
    public bool IsStable { get; init; }
}

/// <summary>
/// Unified action recommendation.
/// </summary>
public readonly struct ActionRecommendation
{
    public ThoughtAction Action { get; init; }
    public float Confidence { get; init; }
    public RecommendationSource Source { get; init; }
    public ObjectThought? Target { get; init; }
    public string Reasoning { get; init; }
}

/// <summary>
/// Source of action recommendation.
/// </summary>
public enum RecommendationSource
{
    Gradient,
    Thought,
    Combined
}
