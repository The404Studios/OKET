namespace OKET.Core.Trust;

/// <summary>
/// Prototype Vault - The Key Vault / CID Store.
///
/// This is where stable gradient objects get COMMITTED.
/// This is where KNOWLEDGE lives.
///
/// CRITICAL PROPERTIES:
/// 1. Prototypes are APPEND-ONLY
/// 2. Old ones decay, but are NOT rewritten
/// 3. NOTHING enters without stabilizing first
///
/// Each entry contains:
/// - Centroid signature vector (μ)
/// - Variance envelope (σ²) - diagonal covariance
/// - Allowed action classes
/// - Known transition outcomes
/// - Confidence decay rules
/// - Context profile (where it usually appears)
///
/// Matching uses diagonal Mahalanobis distance:
/// d²(v,p) = Σᵢ (vᵢ - μᵢ)² / (σᵢ² + ε)
/// S_match = exp(-½ d²)
/// </summary>
public sealed class PrototypeVault
{
    // All prototypes (append-only)
    private readonly List<VaultPrototype> _prototypes = new();
    private readonly Dictionary<string, int> _namedIndex = new();

    // Configuration
    private const float MatchEpsilon = 0.01f;
    private const float MinMatchScoreForAssociation = 0.60f;
    private const float DecayRate = 0.995f; // Per frame

    // Statistics
    private int _nextProtoId;
    private int _totalCommits;
    private int _totalMatches;
    private int _totalNamings;

    public int PrototypeCount => _prototypes.Count;
    public int NamedCount => _namedIndex.Count;
    public int TotalCommits => _totalCommits;
    public int TotalMatches => _totalMatches;

    /// <summary>
    /// Commit a stabilized gradient object to the vault.
    /// Only call this after GradientStabilizer has validated!
    /// </summary>
    public VaultPrototype Commit(SignatureVector signature, ContextProfile context)
    {
        var proto = new VaultPrototype(_nextProtoId++, signature, context);
        _prototypes.Add(proto);
        _totalCommits++;
        return proto;
    }

    /// <summary>
    /// Match a signature to the vault.
    /// Uses diagonal Mahalanobis distance.
    /// </summary>
    public VaultMatch Match(SignatureVector signature)
    {
        _totalMatches++;

        VaultPrototype? bestProto = null;
        float bestScore = 0;
        float bestDistance = float.MaxValue;

        foreach (var proto in _prototypes)
        {
            if (proto.IsDead) continue;

            float d2 = ComputeMahalanobisDistance(signature, proto);
            float score = MathF.Exp(-0.5f * d2);

            if (score > bestScore)
            {
                bestScore = score;
                bestDistance = d2;
                bestProto = proto;
            }
        }

        if (bestProto != null && bestScore >= MinMatchScoreForAssociation)
        {
            return new VaultMatch
            {
                Found = true,
                Prototype = bestProto,
                MatchScore = bestScore,
                Distance = bestDistance,
                PrototypeTrust = bestProto.Trust
            };
        }

        return new VaultMatch { Found = false };
    }

    /// <summary>
    /// Compute diagonal Mahalanobis distance.
    /// d²(v,p) = Σᵢ (vᵢ - μᵢ)² / (σᵢ² + ε)
    /// </summary>
    private static float ComputeMahalanobisDistance(SignatureVector signature, VaultPrototype proto)
    {
        float sum = 0;
        for (int i = 0; i < SignatureVector.Dim; i++)
        {
            float diff = signature.X[i] - proto.Centroid.X[i];
            float var = proto.Variance.X[i] + MatchEpsilon;
            sum += (diff * diff) / var;
        }
        return sum;
    }

    /// <summary>
    /// Update prototype with new observation (online learning).
    /// </summary>
    public void UpdatePrototype(int protoId, SignatureVector signature, float weight = 0.1f)
    {
        var proto = _prototypes.FirstOrDefault(p => p.Id == protoId);
        if (proto == null) return;

        proto.Update(signature, weight);
    }

    /// <summary>
    /// Record action outcome for a prototype.
    /// </summary>
    public void RecordActionOutcome(int protoId, ActionId action, ActionOutcome outcome)
    {
        var proto = _prototypes.FirstOrDefault(p => p.Id == protoId);
        proto?.RecordOutcome(action, outcome);
    }

    /// <summary>
    /// Try to name a prototype (only after stability criteria met).
    /// </summary>
    public bool TryName(int protoId, string name)
    {
        var proto = _prototypes.FirstOrDefault(p => p.Id == protoId);
        if (proto == null) return false;

        if (!proto.CanBeNamed)
            return false;

        proto.AssignName(name);
        _namedIndex[name] = protoId;
        _totalNamings++;
        return true;
    }

    /// <summary>
    /// Get prototype by name.
    /// </summary>
    public VaultPrototype? GetByName(string name)
    {
        if (_namedIndex.TryGetValue(name, out int id))
            return _prototypes.FirstOrDefault(p => p.Id == id);
        return null;
    }

    /// <summary>
    /// Get prototype by ID.
    /// </summary>
    public VaultPrototype? GetById(int id)
    {
        return _prototypes.FirstOrDefault(p => p.Id == id);
    }

    /// <summary>
    /// Decay all prototypes (call once per frame).
    /// Old unused prototypes decay toward death.
    /// </summary>
    public void Decay(long frameId)
    {
        foreach (var proto in _prototypes)
        {
            proto.Decay(DecayRate, frameId);
        }
    }

    /// <summary>
    /// Get expected outcome for an action given a prototype.
    /// </summary>
    public ActionExpectation GetExpectedOutcome(int protoId, ActionId action)
    {
        var proto = _prototypes.FirstOrDefault(p => p.Id == protoId);
        if (proto == null)
            return new ActionExpectation { Confidence = 0 };

        return proto.GetExpectedOutcome(action);
    }

    /// <summary>
    /// Get diagnostics.
    /// </summary>
    public string GetDiagnostics()
    {
        int alive = _prototypes.Count(p => !p.IsDead);
        int named = _prototypes.Count(p => p.IsNamed);
        float avgTrust = _prototypes.Where(p => !p.IsDead).Select(p => p.Trust).DefaultIfEmpty(0).Average();

        return $"""
            === PROTOTYPE VAULT (Key Store) ===
            Total: {_prototypes.Count} (alive={alive}, named={named})
            Commits: {_totalCommits}, Matches: {_totalMatches}
            Avg Trust: {avgTrust:F2}

            Top Prototypes:
            {string.Join("\n", _prototypes
                .Where(p => !p.IsDead)
                .OrderByDescending(p => p.Trust * p.Observations)
                .Take(5)
                .Select(p => $"  {p}"))}
            ===================================
            """;
    }
}

/// <summary>
/// A prototype stored in the vault.
/// </summary>
public sealed class VaultPrototype
{
    private readonly int _id;
    private readonly long _createdFrame;
    private long _lastObservedFrame;

    // Gaussian model in signature space
    private readonly SignatureVector _centroid;
    private readonly SignatureVector _variance;

    // Context profile
    private readonly ContextProfile _context;

    // Trust and stability
    private float _trust;
    private int _observations;
    private float _drift;
    private bool _isNamed;
    private string? _name;
    private bool _isDead;

    // Action statistics
    private readonly Dictionary<ActionId, VaultActionStats> _actionStats = new();

    public int Id => _id;
    public SignatureVector Centroid => _centroid;
    public SignatureVector Variance => _variance;
    public ContextProfile Context => _context;
    public float Trust => _trust;
    public int Observations => _observations;
    public float Drift => _drift;
    public bool IsNamed => _isNamed;
    public string? Name => _name;
    public bool IsDead => _isDead;
    public long LastObservedFrame => _lastObservedFrame;

    /// <summary>Can this prototype be named?</summary>
    public bool CanBeNamed =>
        !_isNamed &&
        _observations >= RootInvariants.MinObservationsForNaming &&
        GetAverageAuthScore() >= RootInvariants.MinAvgAuthScoreForNaming &&
        GetOutcomeConsistency() >= RootInvariants.MinOutcomeConsistency;

    public VaultPrototype(int id, SignatureVector initial, ContextProfile context)
    {
        _id = id;
        _createdFrame = 0; // Will be set properly
        _centroid = initial.Clone();
        _variance = SignatureVector.CreateWithValue(0.1f); // Initial variance
        _context = context;
        _trust = 0.5f;
        _observations = 1;
    }

    /// <summary>
    /// Update centroid and variance with new observation.
    /// </summary>
    public void Update(SignatureVector signature, float learningRate = 0.1f)
    {
        _observations++;

        // Adaptive learning rate (slower as we learn more)
        float adaptiveLr = learningRate / (1f + _observations * 0.01f);

        for (int i = 0; i < SignatureVector.Dim; i++)
        {
            float diff = signature.X[i] - _centroid.X[i];

            // Update centroid (exponential moving average)
            _centroid.X[i] += adaptiveLr * diff;

            // Update variance (Welford's online algorithm simplified)
            _variance.X[i] = _variance.X[i] * (1f - adaptiveLr) +
                            adaptiveLr * diff * diff;
        }

        // Track drift
        _drift = _drift * 0.9f + signature.DistanceTo(_centroid) * 0.1f;
    }

    /// <summary>
    /// Record action outcome.
    /// </summary>
    public void RecordOutcome(ActionId action, ActionOutcome outcome)
    {
        if (!_actionStats.TryGetValue(action, out var stats))
        {
            stats = new VaultActionStats();
            _actionStats[action] = stats;
        }

        stats.Record(outcome);

        // Update trust based on outcome
        if (outcome.Success)
            _trust = Math.Min(1f, _trust + 0.02f);
        else
            _trust = Math.Max(0f, _trust - 0.05f);
    }

    /// <summary>
    /// Get expected outcome for an action.
    /// </summary>
    public ActionExpectation GetExpectedOutcome(ActionId action)
    {
        if (!_actionStats.TryGetValue(action, out var stats))
            return new ActionExpectation { Confidence = 0 };

        return new ActionExpectation
        {
            ExpectedReward = stats.AvgReward,
            ExpectedRisk = stats.AvgRisk,
            ExpectedInfoGain = stats.AvgInfoGain,
            Confidence = stats.Confidence,
            Trials = stats.Trials
        };
    }

    /// <summary>
    /// Decay trust over time if not observed.
    /// </summary>
    public void Decay(float rate, long currentFrame)
    {
        long framesSinceObserved = currentFrame - _lastObservedFrame;
        if (framesSinceObserved > 300 && !_isNamed)
        {
            _trust *= rate;
            if (_trust < 0.05f)
                _isDead = true;
        }
    }

    /// <summary>
    /// Assign a stable name.
    /// </summary>
    public void AssignName(string name)
    {
        _isNamed = true;
        _name = name;
    }

    /// <summary>
    /// Mark as observed this frame.
    /// </summary>
    public void MarkObserved(long frameId)
    {
        _lastObservedFrame = frameId;
    }

    private float GetAverageAuthScore()
    {
        // Simplified - would track auth scores over time
        return _trust;
    }

    private float GetOutcomeConsistency()
    {
        if (_actionStats.Count == 0) return 0;

        float totalConsistency = 0;
        int count = 0;

        foreach (var stats in _actionStats.Values)
        {
            if (stats.Trials >= 3)
            {
                totalConsistency += stats.Confidence;
                count++;
            }
        }

        return count > 0 ? totalConsistency / count : 0;
    }

    public override string ToString()
    {
        string identity = _name ?? $"Proto#{_id}";
        return $"{identity}: trust={_trust:F2} obs={_observations} drift={_drift:F3} " +
               $"named={_isNamed}";
    }
}

/// <summary>
/// 48-dimensional signature vector.
/// </summary>
public sealed class SignatureVector
{
    public const int Dim = 48;
    public float[] X = new float[Dim];
    public float Norm { get; private set; }

    public static SignatureVector CreateWithValue(float value)
    {
        var sv = new SignatureVector();
        Array.Fill(sv.X, value);
        return sv;
    }

    public SignatureVector Clone()
    {
        var clone = new SignatureVector();
        Array.Copy(X, clone.X, Dim);
        clone.Norm = Norm;
        return clone;
    }

    public void ComputeNorm()
    {
        float sum = 0;
        for (int i = 0; i < Dim; i++)
            sum += X[i] * X[i];
        Norm = MathF.Sqrt(sum);
    }

    public float DistanceTo(SignatureVector other)
    {
        float sum = 0;
        for (int i = 0; i < Dim; i++)
        {
            float diff = X[i] - other.X[i];
            sum += diff * diff;
        }
        return MathF.Sqrt(sum);
    }

    /// <summary>
    /// Fill from gradient object properties.
    /// </summary>
    public void FillFromGradient(GradientSignatureInputs inputs)
    {
        // A. Motion (0-5)
        X[0] = inputs.MeanVx;
        X[1] = inputs.MeanVy;
        X[2] = inputs.Speed;
        X[3] = inputs.Acceleration;
        X[4] = inputs.MotionCoherence;
        X[5] = inputs.Jitter;

        // B. Shape/Geometry (6-14)
        X[6] = inputs.AreaNorm;
        X[7] = MapAspect(inputs.AspectRatio);
        X[8] = inputs.Compactness;
        X[9] = inputs.EdgeDensity;
        X[10] = inputs.Hu1;
        X[11] = inputs.Hu2;
        X[12] = inputs.Hu3;
        X[13] = inputs.ContourComplexity;
        X[14] = inputs.VerticalityBias;

        // C. Color (15-26)
        X[15] = inputs.HueMean;
        X[16] = inputs.HueVar;
        X[17] = inputs.SatMean;
        X[18] = inputs.SatVar;
        X[19] = inputs.ValMean;
        X[20] = inputs.ValVar;
        for (int i = 0; i < 6; i++)
            X[21 + i] = inputs.HueHist[i];

        // D. Temporal Stability (27-33)
        X[27] = inputs.Persistence;
        X[28] = inputs.TemporalStability;
        X[29] = inputs.OcclusionRate;
        X[30] = inputs.SignatureDrift;
        X[31] = inputs.ReappearanceRate;
        X[32] = inputs.FrameConsistency;
        X[33] = inputs.LifetimeConfidence;

        // E. Context (34-41)
        X[34] = inputs.Cx;
        X[35] = inputs.Cy;
        X[36] = inputs.RoiId;
        X[37] = inputs.DepthHint;
        X[38] = inputs.ScreenVelocity;
        X[39] = inputs.CameraMotionBias;
        X[40] = inputs.HudOverlap;
        X[41] = inputs.EdgeProximity;

        // F. Quality/Meta (42-47)
        X[42] = inputs.SegmentationQuality;
        X[43] = inputs.SignalToNoise;
        X[44] = inputs.Contrast;
        X[45] = inputs.LightingStability;
        X[46] = inputs.NoveltyScore;
        X[47] = inputs.ProtoConfidence;

        ComputeNorm();
    }

    private static float MapAspect(float aspect)
    {
        // Map [0.2, 5] to [0, 1]
        return Math.Clamp((aspect - 0.2f) / 4.8f, 0f, 1f);
    }
}

/// <summary>
/// Inputs for filling signature vector.
/// </summary>
public struct GradientSignatureInputs
{
    // Motion
    public float MeanVx, MeanVy, Speed, Acceleration, MotionCoherence, Jitter;

    // Shape
    public float AreaNorm, AspectRatio, Compactness, EdgeDensity;
    public float Hu1, Hu2, Hu3, ContourComplexity, VerticalityBias;

    // Color
    public float HueMean, HueVar, SatMean, SatVar, ValMean, ValVar;
    public float[] HueHist;

    // Temporal
    public float Persistence, TemporalStability, OcclusionRate;
    public float SignatureDrift, ReappearanceRate, FrameConsistency, LifetimeConfidence;

    // Context
    public float Cx, Cy, RoiId, DepthHint, ScreenVelocity, CameraMotionBias;
    public float HudOverlap, EdgeProximity;

    // Quality
    public float SegmentationQuality, SignalToNoise, Contrast, LightingStability;
    public float NoveltyScore, ProtoConfidence;
}

/// <summary>
/// Context profile for a prototype.
/// </summary>
public sealed class ContextProfile
{
    public float CxMean { get; set; }
    public float CyMean { get; set; }
    public float CxVar { get; set; }
    public float CyVar { get; set; }
    public int RoiMask { get; set; } // Allowed ROIs bitmask

    /// <summary>
    /// Compute context score for a position.
    /// </summary>
    public float ComputeScore(float cx, float cy, int roiId)
    {
        // Check ROI allowed
        if ((RoiMask & (1 << roiId)) == 0)
            return 0;

        // Gaussian distance in context space
        float dcx = (cx - CxMean) * (cx - CxMean) / (CxVar + 0.01f);
        float dcy = (cy - CyMean) * (cy - CyMean) / (CyVar + 0.01f);

        return MathF.Exp(-0.5f * (dcx + dcy));
    }
}

/// <summary>
/// Action statistics for a prototype.
/// </summary>
internal sealed class VaultActionStats
{
    public int Trials { get; private set; }
    public int Successes { get; private set; }
    public float AvgReward { get; private set; }
    public float AvgRisk { get; private set; }
    public float AvgInfoGain { get; private set; }
    public float Confidence => Trials > 0 ? (float)Successes / Trials : 0;

    public void Record(ActionOutcome outcome)
    {
        Trials++;
        if (outcome.Success) Successes++;

        float lr = 1f / Trials;
        AvgReward = AvgReward * (1 - lr) + outcome.Reward * lr;
        AvgRisk = AvgRisk * (1 - lr) + outcome.Risk * lr;
        AvgInfoGain = AvgInfoGain * (1 - lr) + outcome.InfoGain * lr;
    }
}

/// <summary>
/// Action outcome record.
/// </summary>
public readonly struct ActionOutcome
{
    public bool Success { get; init; }
    public float Reward { get; init; }
    public float Risk { get; init; }
    public float InfoGain { get; init; }
}

/// <summary>
/// Expected outcome for an action.
/// </summary>
public readonly struct ActionExpectation
{
    public float ExpectedReward { get; init; }
    public float ExpectedRisk { get; init; }
    public float ExpectedInfoGain { get; init; }
    public float Confidence { get; init; }
    public int Trials { get; init; }
}

/// <summary>
/// Match result from vault.
/// </summary>
public readonly struct VaultMatch
{
    public bool Found { get; init; }
    public VaultPrototype? Prototype { get; init; }
    public float MatchScore { get; init; }
    public float Distance { get; init; }
    public float PrototypeTrust { get; init; }
}

/// <summary>
/// Action identifier.
/// </summary>
public enum ActionId
{
    Observe,
    Engage,
    Flee,
    Kite,
    Approach,
    Interact,
    Ignore,
    Probe
}
