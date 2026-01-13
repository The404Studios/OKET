using OKET.Core.Gradients;
using OKET.Core.Trust;
using OKET.Core.Detection;
using OKET.Core.Types;

namespace OKET.Core.Intelligence;

/// <summary>
/// Intelligence Bridge - Connects all intelligence subsystems.
///
/// This is the integration point that ties together:
/// - RealTimeIntelligence (detection pipeline)
/// - GradientSystem (perception)
/// - CertificateAuthority (trust)
/// - KnowledgeOrganizer (learning)
///
/// SINGLE ENTRY POINT for the cognitive system.
/// </summary>
public sealed class IntelligenceBridge : IDisposable
{
    // Core systems
    private readonly RealTimeIntelligence _intelligence;
    private readonly GradientSystem _gradientSystem;
    private readonly CertificateAuthority _certificateAuthority;
    private readonly IntelligenceRenderer _renderer;

    // State
    private long _frameCount;
    private IntelligenceFrame? _lastFrame;

    // Configuration
    private readonly BridgeConfig _config;

    public RealTimeIntelligence Intelligence => _intelligence;
    public GradientSystem GradientSystem => _gradientSystem;
    public CertificateAuthority Authority => _certificateAuthority;
    public IntelligenceRenderer Renderer => _renderer;
    public IntelligenceFrame? LastFrame => _lastFrame;

    public IntelligenceBridge(int width, int height, BridgeConfig? config = null)
    {
        _config = config ?? BridgeConfig.Default;

        // Initialize systems
        _intelligence = new RealTimeIntelligence(
            width, height,
            _config.IntelligenceConfig);

        _gradientSystem = new GradientSystem(
            width, height,
            _config.GradientCellSize);

        _certificateAuthority = new CertificateAuthority(
            "OKET_ROOT",
            CertificateLevel.Root);
        _certificateAuthority.InitializeDomainCAs();

        _renderer = new IntelligenceRenderer(_config.RenderStyle);
    }

    /// <summary>
    /// Process a frame through all intelligence systems.
    /// This is the main entry point.
    /// </summary>
    public IntelligenceResult ProcessFrame(
        FrameData frame,
        GameState? gameState = null)
    {
        _frameCount++;

        // === PROCESS THROUGH GRADIENT SYSTEM ===
        float urgency = ComputeUrgency(gameState);
        var gradientResult = _gradientSystem.ProcessFrame(frame, urgency);

        // === PROCESS THROUGH REAL-TIME INTELLIGENCE ===
        var intelligenceFrame = _intelligence.ProcessFrame(frame, gameState);
        _lastFrame = intelligenceFrame;

        // === CROSS-REFERENCE WITH AUTHORITY ===
        foreach (var detection in intelligenceFrame.Detections)
        {
            // Get best certificate for this detection
            var context = CreateCertificateContext(detection, gameState);
            var targetType = GetTargetType(detection);
            var cert = _certificateAuthority.GetBestCertificate(context, targetType);

            if (cert != null)
            {
                // Apply certificate authority decision
                detection.ApplyCertification(new AuthorityCertification
                {
                    Level = CertLevelToTrustLevel(cert.Level),
                    CertifiedClass = cert.Behavior.TargetType,
                    TrustScore = cert.OverrideStrength,
                    ThreatModifier = cert.Behavior.Action == CertifiedAction.Flee ? 1.5f : 1f,
                    OpportunityModifier = cert.Behavior.Action == CertifiedAction.Collect ? 1.5f : 1f,
                    CertificationReason = $"Certificate: {cert.Subject}"
                });
            }
        }

        // === CONVERT TO UNIFIED RESULT ===
        return new IntelligenceResult
        {
            FrameId = _frameCount,
            Timestamp = DateTime.UtcNow,
            Frame = intelligenceFrame,
            GradientResult = gradientResult,
            RecommendedGradientAction = gradientResult.Authorization.AuthorizedAction,
            RecommendedIntelligenceAction = MapToGradientAction(intelligenceFrame.RecommendedAction.Type),
            FinalRecommendedAction = ResolveAction(gradientResult, intelligenceFrame),
            Confidence = (gradientResult.Authorization.Confidence + intelligenceFrame.Confidence) / 2,
            ThreatLevel = intelligenceFrame.ThreatLevel,
            ProcessTimeMs = intelligenceFrame.Detections.Count > 0 ? _intelligence.ProcessTimeMs : 0
        };
    }

    /// <summary>
    /// Ingest external YOLO/ONNX detections.
    /// </summary>
    public void IngestExternalDetections(DetectionResult detections)
    {
        _intelligence.IngestExternalDetections(detections.Detections);
    }

    /// <summary>
    /// Record outcome for learning.
    /// </summary>
    public void RecordOutcome(
        ActionOutcome outcome,
        float successScore,
        float riskIncurred,
        float infoGained,
        bool survived)
    {
        // Feed to real-time intelligence
        _intelligence.RecordOutcome(outcome);

        // Feed to gradient system
        _gradientSystem.RecordOutcome(
            MapToGradientAction(outcome.Action),
            successScore,
            riskIncurred,
            infoGained,
            survived);

        // Feed to certificate authority
        var progress = _certificateAuthority.RecordPendingOutcome(
            $"PENDING_{outcome.DetectionId}",
            outcome.Success > 0);

        // If certificated, log
        if (progress.Status == CertificationStatus.Certified)
        {
            Console.WriteLine($"[Intelligence] Pattern certified: {progress.CertificateId}");
        }
    }

    /// <summary>
    /// Render current detections to bitmap.
    /// </summary>
    public System.Drawing.Bitmap RenderDetections(int width, int height)
    {
        if (_lastFrame == null)
            return new System.Drawing.Bitmap(width, height);

        return _renderer.RenderDetections(_lastFrame.Detections, width, height, _lastFrame);
    }

    /// <summary>
    /// Render directly to graphics context.
    /// </summary>
    public void RenderTo(System.Drawing.Graphics g, int width, int height)
    {
        if (_lastFrame == null) return;

        _renderer.RenderTo(g, _lastFrame.Detections, _lastFrame, width, height);
    }

    /// <summary>
    /// Get all current threats.
    /// </summary>
    public IEnumerable<IntelligentDetection> GetThreats()
    {
        return _intelligence.GetThreats();
    }

    /// <summary>
    /// Get all current opportunities.
    /// </summary>
    public IEnumerable<IntelligentDetection> GetOpportunities()
    {
        return _intelligence.GetOpportunities();
    }

    /// <summary>
    /// Get highest priority target.
    /// </summary>
    public IntelligentDetection? GetPriorityTarget()
    {
        return _intelligence.GetHighestPriority();
    }

    private static float ComputeUrgency(GameState? state)
    {
        if (state == null) return 0.5f;

        float urgency = 0.3f;

        // Low health = high urgency
        if (state.Health < 0.3f)
            urgency += 0.4f;
        else if (state.Health < 0.5f)
            urgency += 0.2f;

        // Low ammo = moderate urgency
        if (state.Ammo < 10)
            urgency += 0.2f;

        return Math.Clamp(urgency, 0f, 1f);
    }

    private static CertificateContext CreateCertificateContext(
        IntelligentDetection detection,
        GameState? state)
    {
        return new CertificateContext
        {
            Health = state?.Health ?? 1f,
            ThreatLevel = detection.ThreatScore,
            HasAmmo = state?.Ammo > 0,
            InCombat = detection.IsThreat,
            CustomData = new Dictionary<string, float>
            {
                ["detection_confidence"] = detection.Confidence,
                ["detection_speed"] = detection.Speed
            }
        };
    }

    private static string GetTargetType(IntelligentDetection detection)
    {
        if (detection.IsThreat) return "Threat";
        if (detection.IsOpportunity) return "Resource";
        return "Unknown";
    }

    private static TrustLevel CertLevelToTrustLevel(CertificateLevel level)
    {
        return level switch
        {
            CertificateLevel.Root => TrustLevel.Absolute,
            CertificateLevel.Domain => TrustLevel.Trusted,
            CertificateLevel.Pattern => TrustLevel.Certified,
            CertificateLevel.Instance => TrustLevel.Provisional,
            _ => TrustLevel.Unknown
        };
    }

    private static Gradients.ActionType MapToGradientAction(ActionType type)
    {
        return type switch
        {
            ActionType.Observe => Gradients.ActionType.Observe,
            ActionType.Engage => Gradients.ActionType.Engage,
            ActionType.Retreat => Gradients.ActionType.Retreat,
            ActionType.Kite => Gradients.ActionType.Kite,
            ActionType.Interact => Gradients.ActionType.Interact,
            _ => Gradients.ActionType.Observe
        };
    }

    private static Gradients.ActionType ResolveAction(
        GradientCycleResult gradientResult,
        IntelligenceFrame intelligenceFrame)
    {
        // If both agree, use that
        var gradientAction = gradientResult.Authorization.AuthorizedAction;
        var intelligenceAction = MapToGradientAction(intelligenceFrame.RecommendedAction.Type);

        if (gradientAction == intelligenceAction)
            return gradientAction;

        // If gradient has higher confidence, use gradient
        if (gradientResult.Authorization.Confidence > intelligenceFrame.Confidence)
            return gradientAction;

        // Otherwise use intelligence
        return intelligenceAction;
    }

    public void Reset()
    {
        _intelligence.Reset();
        _gradientSystem.Reset();
        _frameCount = 0;
        _lastFrame = null;
    }

    public void Dispose()
    {
        _intelligence.Dispose();
    }
}

/// <summary>
/// Bridge configuration.
/// </summary>
public sealed class BridgeConfig
{
    public IntelligenceConfig IntelligenceConfig { get; init; } = IntelligenceConfig.Default;
    public int GradientCellSize { get; init; } = 16;
    public RenderStyle RenderStyle { get; init; } = RenderStyle.Default;

    public static BridgeConfig Default => new();

    public static BridgeConfig HighPerformance => new()
    {
        IntelligenceConfig = IntelligenceConfig.HighPerformance,
        GradientCellSize = 32,
        RenderStyle = RenderStyle.Minimal
    };
}

/// <summary>
/// Combined result from all intelligence systems.
/// </summary>
public sealed class IntelligenceResult
{
    public long FrameId { get; init; }
    public DateTime Timestamp { get; init; }
    public IntelligenceFrame Frame { get; init; } = null!;
    public GradientCycleResult GradientResult { get; init; }
    public Gradients.ActionType RecommendedGradientAction { get; init; }
    public Gradients.ActionType RecommendedIntelligenceAction { get; init; }
    public Gradients.ActionType FinalRecommendedAction { get; init; }
    public float Confidence { get; init; }
    public float ThreatLevel { get; init; }
    public float ProcessTimeMs { get; init; }

    // Convenience accessors
    public int DetectionCount => Frame.DetectionCount;
    public int ThreatCount => Frame.ThreatCount;
    public IReadOnlyList<IntelligentDetection> Detections => Frame.Detections;
    public IReadOnlyList<KnowledgeTag> Tags => Frame.Tags;
}

/// <summary>
/// High-level action type for intelligence system.
/// Maps to gradient action types.
/// </summary>
public enum ActionType
{
    Observe,
    Engage,
    Approach,
    Retreat,
    Kite,
    Interact,
    Wait,
    Explore
}
