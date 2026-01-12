namespace OKET.Core.Trust;

/// <summary>
/// Gradient Object Stabilizer - The Secure Enclave.
///
/// This is where raw perception becomes ELIGIBLE for meaning.
/// This layer is ISOLATED:
/// - NO actions
/// - NO memory writes
/// - NO naming
/// Just VALIDATION.
///
/// A GradientObject is allowed to exist only if:
/// 1. Its internal gradients agree
/// 2. It persists over N frames
/// 3. Its motion is physically plausible
/// 4. Its signal-to-noise ratio is above threshold
///
/// If it fails → DISCARDED. No exceptions.
///
/// This implements the 3-stage rejection ladder:
/// Stage A: Hard gates (instant discard) - eliminates 60-80% of junk
/// Stage B: Cross-signal agreement (2 of 3 must agree)
/// Stage C: Temporal majority vote (anti-flicker)
/// </summary>
public sealed class GradientStabilizer
{
    // Tracking for temporal validation
    private readonly Dictionary<int, StabilizationTracker> _trackers = new();
    private readonly HashSet<int> _authorizedIds = new();
    private readonly HashSet<int> _rejectedIds = new();

    // Negative prototypes (known bad patterns)
    private readonly List<NegativePrototype> _negativePrototypes = new();

    // Statistics
    private int _totalReceived;
    private int _stageARejects;
    private int _stageBRejects;
    private int _stageCRejects;
    private int _negativeRejects;
    private int _authorized;

    public int TotalReceived => _totalReceived;
    public int StageARejects => _stageARejects;
    public int StageBRejects => _stageBRejects;
    public int StageCRejects => _stageCRejects;
    public int Authorized => _authorized;
    public float RejectRate => _totalReceived > 0
        ? 1f - (float)_authorized / _totalReceived
        : 0;

    public GradientStabilizer()
    {
        InitializeNegativePrototypes();
    }

    /// <summary>
    /// Initialize known bad patterns (UI false positives, shadows, etc.)
    /// </summary>
    private void InitializeNegativePrototypes()
    {
        // UI False Positives (high saturation, static, screen edge)
        _negativePrototypes.Add(new NegativePrototype
        {
            Name = "UiFalsePositive",
            Predicates = new Func<StabilizationInput, float>[]
            {
                i => i.HudOverlap > 0.3f ? 0.8f : 0f,
                i => i.IsStatic && i.Saturation > 0.7f ? 0.6f : 0f,
                i => (i.CenterY < 0.1f || i.CenterY > 0.9f) && i.IsStatic ? 0.5f : 0f
            },
            PenaltyMultiplier = 0.3f
        });

        // Shadow edges (low saturation, edge-like, moves with camera)
        _negativePrototypes.Add(new NegativePrototype
        {
            Name = "ShadowEdge",
            Predicates = new Func<StabilizationInput, float>[]
            {
                i => i.Saturation < 0.1f && i.EdgeDensity > 0.5f ? 0.7f : 0f,
                i => i.CameraMotionBias > 0.5f ? 0.5f : 0f
            },
            PenaltyMultiplier = 0.4f
        });

        // Compression artifacts (flickery, low coherence)
        _negativePrototypes.Add(new NegativePrototype
        {
            Name = "CompressionArtifact",
            Predicates = new Func<StabilizationInput, float>[]
            {
                i => i.MotionCoherence < 0.2f && i.IsMoving ? 0.8f : 0f,
                i => i.SignatureDrift > 0.3f ? 0.6f : 0f
            },
            PenaltyMultiplier = 0.2f
        });
    }

    /// <summary>
    /// Attempt to stabilize a gradient object.
    /// Returns stabilization result with authorization score.
    /// </summary>
    public StabilizationResult Stabilize(StabilizationInput input, long frameId)
    {
        _totalReceived++;

        // Get or create tracker
        if (!_trackers.TryGetValue(input.ObjectId, out var tracker))
        {
            tracker = new StabilizationTracker(input.ObjectId, frameId);
            _trackers[input.ObjectId] = tracker;
        }

        // === STAGE A: HARD GATES (instant discard) ===
        var stageAResult = ValidateStageA(input);
        if (!stageAResult.Passed)
        {
            _stageARejects++;
            tracker.RecordRejection(StabilizationStage.A, stageAResult.Reason);
            return StabilizationResult.Rejected(stageAResult.Reason, StabilizationStage.A);
        }

        // === STAGE B: CROSS-SIGNAL AGREEMENT (2 of 3) ===
        var stageBResult = ValidateStageB(input, tracker);
        if (!stageBResult.Passed)
        {
            _stageBRejects++;
            tracker.RecordRejection(StabilizationStage.B, stageBResult.Reason);
            return StabilizationResult.Rejected(stageBResult.Reason, StabilizationStage.B);
        }

        // === STAGE C: TEMPORAL MAJORITY VOTE (anti-flicker) ===
        var stageCResult = ValidateStageC(input, tracker, frameId);
        if (!stageCResult.Passed)
        {
            _stageCRejects++;
            tracker.RecordRejection(StabilizationStage.C, stageCResult.Reason);
            return StabilizationResult.Rejected(stageCResult.Reason, StabilizationStage.C);
        }

        // === NEGATIVE PROTOTYPE CHECK ===
        float negativePenalty = CheckNegativePrototypes(input);
        if (negativePenalty > 0.7f)
        {
            _negativeRejects++;
            return StabilizationResult.Rejected("Matches negative prototype", StabilizationStage.Negative);
        }

        // === COMPUTE ROOT VALIDATION ===
        var rootInputs = new RootInvariantInputs
        {
            SegmentationQuality = input.SegmentationQuality,
            AreaNorm = input.AreaNorm,
            SignalToNoise = input.SignalToNoise,
            TeleportJump = tracker.LastTeleportJump,
            IsMoving = input.Speed > 0.05f,
            MotionCoherence = input.MotionCoherence,
            Jitter = input.Jitter,
            TemporalStability = input.TemporalStability,
            EdgeDensity = input.EdgeDensity,
            Persistence = tracker.Persistence
        };

        var rootValidation = RootInvariants.ValidateRootInvariants(rootInputs);
        if (!rootValidation.IsValid)
        {
            return StabilizationResult.Rejected(rootValidation.RejectReason!, StabilizationStage.Root);
        }

        // === STABILIZATION SUCCESSFUL ===
        _authorized++;
        tracker.RecordSuccess(frameId, rootValidation.Score);
        _authorizedIds.Add(input.ObjectId);

        // Apply negative penalty to score
        float adjustedScore = rootValidation.Score * (1f - negativePenalty);

        return StabilizationResult.Stabilized(
            adjustedScore,
            tracker.StabilityScore,
            tracker.ConsecutiveSuccesses,
            tracker.IsFullyStabilized);
    }

    /// <summary>
    /// Stage A: Hard gates - instant discard.
    /// Eliminates 60-80% of junk immediately.
    /// </summary>
    private static StageValidation ValidateStageA(StabilizationInput input)
    {
        if (input.SegmentationQuality < RootInvariants.MinSegmentationQuality)
            return StageValidation.Fail($"Segmentation quality {input.SegmentationQuality:F2} < {RootInvariants.MinSegmentationQuality}");

        if (input.AreaNorm < RootInvariants.MinAreaNorm)
            return StageValidation.Fail($"Area {input.AreaNorm:F4} < {RootInvariants.MinAreaNorm}");

        if (input.SignalToNoise < RootInvariants.MinSignalToNoise)
            return StageValidation.Fail($"SNR {input.SignalToNoise:F2} < {RootInvariants.MinSignalToNoise}");

        if (input.IsMoving && input.MotionCoherence < RootInvariants.MinMotionCoherence)
            return StageValidation.Fail($"Motion coherence {input.MotionCoherence:F2} < {RootInvariants.MinMotionCoherence}");

        if (input.EdgeDensity < RootInvariants.MinEdgeDensity)
            return StageValidation.Fail($"Edge density {input.EdgeDensity:F2} < {RootInvariants.MinEdgeDensity}");

        if (input.TemporalStability < RootInvariants.MinTemporalStability && !input.IsFlashEvent)
            return StageValidation.Fail($"Temporal stability {input.TemporalStability:F2} < {RootInvariants.MinTemporalStability}");

        return StageValidation.Pass();
    }

    /// <summary>
    /// Stage B: Cross-signal agreement - 2 of 3 must agree.
    /// </summary>
    private static StageValidation ValidateStageB(StabilizationInput input, StabilizationTracker tracker)
    {
        int agreements = 0;

        // Signal 1: Color coherence
        if (tracker.HasPrototypeMatch)
        {
            if (input.ColorAgreement > 0.5f) agreements++;
        }
        else
        {
            // No prototype - check internal color coherence
            if (input.Saturation > 0.1f || input.Value > 0.2f) agreements++;
        }

        // Signal 2: Shape/edge coherence
        if (input.EdgeDensity > 0.3f && input.SegmentationQuality > 0.6f)
            agreements++;

        // Signal 3: Motion coherence
        if (!input.IsMoving || input.MotionCoherence > 0.4f)
            agreements++;

        if (agreements < 2)
            return StageValidation.Fail($"Cross-signal agreement {agreements}/3 < 2");

        return StageValidation.Pass();
    }

    /// <summary>
    /// Stage C: Temporal majority vote - anti-flicker.
    /// Must be authorized in K of last N frames.
    /// </summary>
    private static StageValidation ValidateStageC(
        StabilizationInput input,
        StabilizationTracker tracker,
        long frameId)
    {
        // Record this frame
        tracker.RecordFrame(frameId, input.SignatureDrift);

        // Check temporal consistency
        int validFrames = tracker.GetValidFrameCount(6);

        if (validFrames < 4 && tracker.TotalFrames >= 6)
            return StageValidation.Fail($"Temporal vote {validFrames}/6 < 4 (flickering)");

        // Check signature drift
        float medianDrift = tracker.GetMedianDrift(6);
        if (medianDrift > RootInvariants.MaxSignatureDrift)
            return StageValidation.Fail($"Signature drift {medianDrift:F3} > {RootInvariants.MaxSignatureDrift}");

        return StageValidation.Pass();
    }

    /// <summary>
    /// Check against negative prototypes (known bad patterns).
    /// Returns penalty score [0, 1].
    /// </summary>
    private float CheckNegativePrototypes(StabilizationInput input)
    {
        float maxPenalty = 0;

        foreach (var negProto in _negativePrototypes)
        {
            float matchScore = 0;
            int matchCount = 0;

            foreach (var predicate in negProto.Predicates)
            {
                float score = predicate(input);
                if (score > 0)
                {
                    matchScore += score;
                    matchCount++;
                }
            }

            if (matchCount >= 2) // At least 2 predicates match
            {
                float avgScore = matchScore / negProto.Predicates.Length;
                float penalty = avgScore * negProto.PenaltyMultiplier;
                maxPenalty = Math.Max(maxPenalty, penalty);
            }
        }

        return maxPenalty;
    }

    /// <summary>
    /// Clean up old trackers.
    /// </summary>
    public void Cleanup(long currentFrame, int maxAge = 300)
    {
        var toRemove = _trackers
            .Where(kv => currentFrame - kv.Value.LastFrameId > maxAge)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in toRemove)
        {
            _trackers.Remove(id);
            _authorizedIds.Remove(id);
        }
    }

    /// <summary>
    /// Check if an object is currently authorized.
    /// </summary>
    public bool IsAuthorized(int objectId)
    {
        return _authorizedIds.Contains(objectId);
    }

    /// <summary>
    /// Get tracker for an object.
    /// </summary>
    public StabilizationTracker? GetTracker(int objectId)
    {
        return _trackers.GetValueOrDefault(objectId);
    }

    /// <summary>
    /// Get diagnostics.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"""
            === GRADIENT STABILIZER (Secure Enclave) ===
            Received: {_totalReceived}
            Authorized: {_authorized} ({(float)_authorized / Math.Max(1, _totalReceived):P1})
            Rejected: Stage A={_stageARejects}, B={_stageBRejects}, C={_stageCRejects}, Neg={_negativeRejects}
            Active Trackers: {_trackers.Count}
            Authorized IDs: {_authorizedIds.Count}
            =============================================
            """;
    }
}

/// <summary>
/// Input for stabilization.
/// </summary>
public readonly struct StabilizationInput
{
    public int ObjectId { get; init; }

    // Stage A inputs
    public float SegmentationQuality { get; init; }
    public float AreaNorm { get; init; }
    public float SignalToNoise { get; init; }
    public float MotionCoherence { get; init; }
    public float EdgeDensity { get; init; }
    public float TemporalStability { get; init; }
    public bool IsMoving { get; init; }
    public bool IsFlashEvent { get; init; }

    // Stage B inputs
    public float ColorAgreement { get; init; }
    public float Saturation { get; init; }
    public float Value { get; init; }

    // Stage C inputs
    public float SignatureDrift { get; init; }

    // Negative prototype inputs
    public float HudOverlap { get; init; }
    public bool IsStatic { get; init; }
    public float CenterX { get; init; }
    public float CenterY { get; init; }
    public float CameraMotionBias { get; init; }
    public float Speed { get; init; }
    public float Jitter { get; init; }
}

/// <summary>
/// Result of stabilization.
/// </summary>
public readonly struct StabilizationResult
{
    public bool IsStabilized { get; init; }
    public float RootScore { get; init; }
    public float StabilityScore { get; init; }
    public int ConsecutiveSuccesses { get; init; }
    public bool IsFullyStabilized { get; init; }
    public string? RejectReason { get; init; }
    public StabilizationStage RejectedAt { get; init; }

    public static StabilizationResult Rejected(string reason, StabilizationStage stage) =>
        new()
        {
            IsStabilized = false,
            RejectReason = reason,
            RejectedAt = stage
        };

    public static StabilizationResult Stabilized(
        float rootScore,
        float stabilityScore,
        int consecutiveSuccesses,
        bool fullyStabilized) =>
        new()
        {
            IsStabilized = true,
            RootScore = rootScore,
            StabilityScore = stabilityScore,
            ConsecutiveSuccesses = consecutiveSuccesses,
            IsFullyStabilized = fullyStabilized
        };
}

/// <summary>
/// Stabilization rejection stage.
/// </summary>
public enum StabilizationStage
{
    None,
    A,       // Hard gates
    B,       // Cross-signal
    C,       // Temporal
    Negative, // Negative prototype
    Root     // Root invariants
}

/// <summary>
/// Stage validation result.
/// </summary>
internal readonly struct StageValidation
{
    public bool Passed { get; init; }
    public string? Reason { get; init; }

    public static StageValidation Pass() => new() { Passed = true };
    public static StageValidation Fail(string reason) => new() { Passed = false, Reason = reason };
}

/// <summary>
/// Tracks stabilization history for an object.
/// </summary>
public sealed class StabilizationTracker
{
    private readonly int _objectId;
    private readonly long _firstFrameId;
    private long _lastFrameId;
    private readonly Queue<(long frame, bool valid, float drift)> _frameHistory = new();
    private const int MaxHistory = 30;

    private int _consecutiveSuccesses;
    private int _totalSuccesses;
    private int _totalRejections;
    private float _cumulativeRootScore;
    private float _lastCenterX, _lastCenterY;

    public int ObjectId => _objectId;
    public long FirstFrameId => _firstFrameId;
    public long LastFrameId => _lastFrameId;
    public int TotalFrames => _frameHistory.Count;
    public int ConsecutiveSuccesses => _consecutiveSuccesses;
    public float StabilityScore => TotalFrames > 0
        ? (float)_totalSuccesses / TotalFrames
        : 0;
    public float Persistence => TotalFrames / 30f; // Normalized to ~1 second
    public float LastTeleportJump { get; private set; }
    public bool HasPrototypeMatch { get; set; }
    public bool IsFullyStabilized => _consecutiveSuccesses >= 10 && StabilityScore > 0.8f;

    public StabilizationTracker(int objectId, long frameId)
    {
        _objectId = objectId;
        _firstFrameId = frameId;
        _lastFrameId = frameId;
    }

    public void RecordFrame(long frameId, float drift)
    {
        _lastFrameId = frameId;
        _frameHistory.Enqueue((frameId, false, drift)); // Will be updated
        while (_frameHistory.Count > MaxHistory)
            _frameHistory.Dequeue();
    }

    public void RecordSuccess(long frameId, float rootScore)
    {
        _consecutiveSuccesses++;
        _totalSuccesses++;
        _cumulativeRootScore += rootScore;

        // Update last frame as valid
        if (_frameHistory.Count > 0)
        {
            var frames = _frameHistory.ToArray();
            if (frames.Length > 0 && frames[^1].frame == frameId)
            {
                _frameHistory.Clear();
                foreach (var (f, _, d) in frames.Take(frames.Length - 1))
                    _frameHistory.Enqueue((f, false, d));
                _frameHistory.Enqueue((frameId, true, frames[^1].drift));
            }
        }
    }

    public void RecordRejection(StabilizationStage stage, string? reason)
    {
        _consecutiveSuccesses = 0;
        _totalRejections++;
    }

    public void RecordPosition(float cx, float cy, float deltaTime)
    {
        if (deltaTime > 0)
        {
            float dx = cx - _lastCenterX;
            float dy = cy - _lastCenterY;
            LastTeleportJump = MathF.Sqrt(dx * dx + dy * dy) / deltaTime;
        }
        _lastCenterX = cx;
        _lastCenterY = cy;
    }

    public int GetValidFrameCount(int lastN)
    {
        return _frameHistory.TakeLast(lastN).Count(f => f.valid);
    }

    public float GetMedianDrift(int lastN)
    {
        var drifts = _frameHistory.TakeLast(lastN).Select(f => f.drift).OrderBy(x => x).ToList();
        if (drifts.Count == 0) return 0;
        return drifts[drifts.Count / 2];
    }
}

/// <summary>
/// Negative prototype (known bad pattern).
/// </summary>
internal sealed class NegativePrototype
{
    public string Name { get; init; } = "";
    public Func<StabilizationInput, float>[] Predicates { get; init; } = Array.Empty<Func<StabilizationInput, float>>();
    public float PenaltyMultiplier { get; init; } = 0.5f;
}
