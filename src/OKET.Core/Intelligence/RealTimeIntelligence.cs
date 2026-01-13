using OKET.Core.Detection;
using OKET.Core.Gradients;
using OKET.Core.Trust;
using OKET.Core.Types;

namespace OKET.Core.Intelligence;

/// <summary>
/// Real-Time Intelligence System - The unified perception and decision pipeline.
///
/// This is the SINGLE SOURCE OF TRUTH for all detections, authorizations, and learnings.
///
/// PIPELINE:
///   Frame → Gradient Analysis → Object Detection → Authority Certification → Knowledge Extraction
///                                    ↓
///                         Bounding Boxes + Tags
///                                    ↓
///                              Visualization
///
/// PRINCIPLES:
/// 1. Real-time: Every frame produces fresh detections
/// 2. Unified: One system, one truth
/// 3. Observable: Everything is tagged and renderable
/// 4. Learning: Every detection informs future patterns
/// </summary>
public sealed class RealTimeIntelligence : IDisposable
{
    // Core components
    private readonly GradientField _field;
    private readonly IntelligenceTracker _tracker;
    private readonly AuthorityCertifier _certifier;
    private readonly KnowledgeExtractor _knowledgeExtractor;

    // Current state
    private readonly List<IntelligentDetection> _detections = new();
    private readonly List<KnowledgeTag> _extractedTags = new();
    private IntelligenceFrame _currentFrame;
    private long _frameId;

    // Performance tracking
    private readonly RollingAverage _processTimeMs = new(60);
    private float _lastProcessTime;

    // Configuration
    private readonly IntelligenceConfig _config;

    public IReadOnlyList<IntelligentDetection> Detections => _detections;
    public IReadOnlyList<KnowledgeTag> ExtractedTags => _extractedTags;
    public IntelligenceFrame CurrentFrame => _currentFrame;
    public long FrameId => _frameId;
    public float ProcessTimeMs => _lastProcessTime;
    public float AverageProcessTimeMs => _processTimeMs.Average;

    public RealTimeIntelligence(int width, int height, IntelligenceConfig? config = null)
    {
        _config = config ?? IntelligenceConfig.Default;
        _field = new GradientField(width, height, _config.CellSize);
        _tracker = new IntelligenceTracker(_config);
        _certifier = new AuthorityCertifier();
        _knowledgeExtractor = new KnowledgeExtractor();
    }

    /// <summary>
    /// Process a frame and extract all intelligence.
    /// This is the main entry point - call once per frame.
    /// </summary>
    public IntelligenceFrame ProcessFrame(FrameData frame, GameState? gameState = null)
    {
        var startTime = DateTime.UtcNow;
        _frameId++;

        // Clear previous frame data
        _detections.Clear();
        _extractedTags.Clear();

        // === STAGE 1: GRADIENT FIELD UPDATE ===
        _field.Update(frame, _frameId);

        // === STAGE 2: OBJECT DETECTION & TRACKING ===
        var rawDetections = _tracker.Track(_field, _frameId);

        // === STAGE 3: AUTHORITY CERTIFICATION ===
        foreach (var detection in rawDetections)
        {
            var certification = _certifier.Certify(detection, gameState);
            detection.ApplyCertification(certification);
            _detections.Add(detection);
        }

        // === STAGE 4: KNOWLEDGE EXTRACTION ===
        var tags = _knowledgeExtractor.Extract(_detections, _frameId);
        _extractedTags.AddRange(tags);

        // === STAGE 5: BUILD FRAME RESULT ===
        _currentFrame = new IntelligenceFrame
        {
            FrameId = _frameId,
            Timestamp = DateTime.UtcNow,
            Detections = _detections.ToList(),
            Tags = _extractedTags.ToList(),
            ThreatLevel = ComputeThreatLevel(),
            OpportunityLevel = ComputeOpportunityLevel(),
            Confidence = ComputeOverallConfidence(),
            RecommendedAction = DetermineRecommendedAction(gameState)
        };

        // Track performance
        _lastProcessTime = (float)(DateTime.UtcNow - startTime).TotalMilliseconds;
        _processTimeMs.Add(_lastProcessTime);

        return _currentFrame;
    }

    /// <summary>
    /// Feed external YOLO/ONNX detections into the system.
    /// Use this when you have pre-computed detections from a neural network.
    /// </summary>
    public void IngestExternalDetections(IEnumerable<Detection.Detection> externalDetections)
    {
        foreach (var external in externalDetections)
        {
            // Convert external detection to intelligent detection
            var intelligent = IntelligentDetection.FromExternal(external, _frameId);

            // Run through tracker to assign IDs and compute velocity
            _tracker.IngestExternal(intelligent);

            // Certify with authority
            var certification = _certifier.Certify(intelligent, null);
            intelligent.ApplyCertification(certification);

            _detections.Add(intelligent);
        }
    }

    /// <summary>
    /// Record outcome feedback for learning.
    /// Call this when you know the result of an action.
    /// </summary>
    public void RecordOutcome(ActionOutcome outcome)
    {
        _knowledgeExtractor.RecordOutcome(outcome);
        _certifier.RecordOutcome(outcome);
    }

    /// <summary>
    /// Get all detections classified as threats.
    /// </summary>
    public IEnumerable<IntelligentDetection> GetThreats()
    {
        return _detections.Where(d => d.IsThreat && d.TrustLevel >= TrustLevel.Certified);
    }

    /// <summary>
    /// Get all detections classified as opportunities (items, resources).
    /// </summary>
    public IEnumerable<IntelligentDetection> GetOpportunities()
    {
        return _detections.Where(d => d.IsOpportunity);
    }

    /// <summary>
    /// Get the highest priority detection.
    /// </summary>
    public IntelligentDetection? GetHighestPriority()
    {
        return _detections.MaxBy(d => d.Priority);
    }

    /// <summary>
    /// Get detections near a screen position.
    /// </summary>
    public IEnumerable<IntelligentDetection> GetNear(Vector2 position, float radius)
    {
        return _detections.Where(d =>
            Vector2.Distance(d.BoundingBox.Center, position) < radius);
    }

    private float ComputeThreatLevel()
    {
        var threats = GetThreats().ToList();
        if (threats.Count == 0) return 0;

        float totalThreat = threats.Sum(t => t.ThreatScore * t.Confidence);
        return Math.Clamp(totalThreat / (1 + threats.Count * 0.5f), 0, 1);
    }

    private float ComputeOpportunityLevel()
    {
        var opportunities = GetOpportunities().ToList();
        if (opportunities.Count == 0) return 0;

        float totalOpp = opportunities.Sum(o => o.OpportunityScore * o.Confidence);
        return Math.Clamp(totalOpp / (1 + opportunities.Count * 0.3f), 0, 1);
    }

    private float ComputeOverallConfidence()
    {
        if (_detections.Count == 0) return 1f; // No detections = confident nothing's there
        return _detections.Average(d => d.Confidence);
    }

    private RecommendedAction DetermineRecommendedAction(GameState? state)
    {
        var threatLevel = ComputeThreatLevel();
        var oppLevel = ComputeOpportunityLevel();
        var health = state?.Health ?? 1f;

        // Critical health - always flee
        if (health < 0.15f && threatLevel > 0.3f)
        {
            return new RecommendedAction
            {
                Type = ActionType.Retreat,
                Priority = 1f,
                Reason = "Critical health with active threats",
                Target = null
            };
        }

        // High threat - engage or kite
        if (threatLevel > 0.6f)
        {
            var hasAmmo = state?.Ammo > 0;
            if (hasAmmo == true && health > 0.4f)
            {
                return new RecommendedAction
                {
                    Type = ActionType.Engage,
                    Priority = 0.9f,
                    Reason = "Multiple threats, engaging",
                    Target = GetHighestPriority()
                };
            }

            return new RecommendedAction
            {
                Type = ActionType.Kite,
                Priority = 0.85f,
                Reason = "Multiple threats, kiting",
                Target = GetHighestPriority()
            };
        }

        // Opportunity available
        if (oppLevel > 0.3f && threatLevel < 0.3f)
        {
            return new RecommendedAction
            {
                Type = ActionType.Interact,
                Priority = 0.7f,
                Reason = "Safe to collect resources",
                Target = GetOpportunities().MaxBy(o => o.OpportunityScore)
            };
        }

        // Default - observe
        return new RecommendedAction
        {
            Type = ActionType.Observe,
            Priority = 0.3f,
            Reason = "Scanning environment",
            Target = null
        };
    }

    public void Reset()
    {
        _detections.Clear();
        _extractedTags.Clear();
        _tracker.Reset();
        _frameId = 0;
    }

    public void Dispose()
    {
        Reset();
    }
}

/// <summary>
/// Configuration for the intelligence system.
/// </summary>
public sealed class IntelligenceConfig
{
    public int CellSize { get; init; } = 16;
    public float MinConfidence { get; init; } = 0.3f;
    public float MinArea { get; init; } = 100f;
    public int MaxTrackedObjects { get; init; } = 50;
    public float TrackingIoUThreshold { get; init; } = 0.3f;
    public int MaxLostFrames { get; init; } = 30;
    public bool EnableKnowledgeExtraction { get; init; } = true;
    public bool EnableAuthorityCertification { get; init; } = true;

    public static IntelligenceConfig Default => new();

    public static IntelligenceConfig HighPerformance => new()
    {
        CellSize = 32,
        MaxTrackedObjects = 20,
        MaxLostFrames = 15,
        EnableKnowledgeExtraction = false
    };
}

/// <summary>
/// Result of processing a single frame.
/// </summary>
public sealed class IntelligenceFrame
{
    public long FrameId { get; init; }
    public DateTime Timestamp { get; init; }
    public List<IntelligentDetection> Detections { get; init; } = new();
    public List<KnowledgeTag> Tags { get; init; } = new();
    public float ThreatLevel { get; init; }
    public float OpportunityLevel { get; init; }
    public float Confidence { get; init; }
    public RecommendedAction RecommendedAction { get; init; }

    public int DetectionCount => Detections.Count;
    public int ThreatCount => Detections.Count(d => d.IsThreat);
    public int OpportunityCount => Detections.Count(d => d.IsOpportunity);
}

/// <summary>
/// Recommended action from the intelligence system.
/// </summary>
public sealed class RecommendedAction
{
    public ActionType Type { get; init; }
    public float Priority { get; init; }
    public string Reason { get; init; } = "";
    public IntelligentDetection? Target { get; init; }
}

/// <summary>
/// Game state for context-aware decisions.
/// </summary>
public sealed class GameState
{
    public float Health { get; init; } = 1f;
    public float Armor { get; init; }
    public int Ammo { get; init; } = 100;
    public int AmmoReserve { get; init; }
    public bool IsReloading { get; init; }
    public Vector2 PlayerPosition { get; init; }
    public float PlayerRotation { get; init; }
}

/// <summary>
/// Outcome of an action for learning feedback.
/// </summary>
public sealed class ActionOutcome
{
    public ActionType Action { get; init; }
    public float Success { get; init; } // -1 to 1
    public float DamageDealt { get; init; }
    public float DamageTaken { get; init; }
    public bool TargetKilled { get; init; }
    public bool ItemCollected { get; init; }
    public long DetectionId { get; init; }
}

/// <summary>
/// Rolling average calculator.
/// </summary>
internal sealed class RollingAverage
{
    private readonly Queue<float> _values;
    private readonly int _maxSize;

    public float Average => _values.Count > 0 ? _values.Average() : 0;

    public RollingAverage(int maxSize)
    {
        _maxSize = maxSize;
        _values = new Queue<float>(maxSize);
    }

    public void Add(float value)
    {
        _values.Enqueue(value);
        while (_values.Count > _maxSize)
            _values.Dequeue();
    }
}
