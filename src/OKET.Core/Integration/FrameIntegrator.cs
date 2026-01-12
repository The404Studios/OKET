namespace OKET.Core.Integration;

/// <summary>
/// The CENTER - neutral opposite of left (global/predictive) and right (local/perceptual).
///
/// This is NOT a mediator or referee.
/// This is the TRANSFORMATION FUNCTION between reference frames.
///
/// Like the coordinate transform between r_P = (x,y,z) and r*_P = (u,v,w):
/// - Local frame (right): perception, feeling, real-time, immediate
/// - Global frame (left): prediction, pattern, compressed, anticipatory
/// - Center: computes the coherence of mapping between frames
///
/// The center answers ONE question:
/// "Given both frames, what directions remain VIABLE?"
///
/// Not true. Not optimal. VIABLE.
///
/// CRITICAL LAWS:
/// 1. Neither frame owns truth - synchronization does
/// 2. Center computes PERMISSION, not decisions
/// 3. Direction is input, not output
/// 4. Modulation flows to BOTH sides equally
/// </summary>
public sealed class FrameIntegrator
{
    // Local frame state (right brain - perception/feeling)
    private LocalFrameState _local;

    // Global frame state (left brain - prediction/pattern)
    private GlobalFrameState _global;

    // Directional bias (vector input to the center)
    private DirectionalBias _bias;

    // Integration output
    private IntegrationState _integration;

    // History for trend calculation
    private readonly Queue<float> _coherenceHistory = new();
    private const int MaxHistory = 30;

    /// <summary>
    /// Current coherence of the frame mapping [0, 1].
    /// High = frames agree, transformation is stable.
    /// Low = frames disagree, transformation is unstable.
    /// </summary>
    public float Coherence => _integration.Coherence;

    /// <summary>
    /// Current permission level [0, 1].
    /// High = action is viable given both frames.
    /// Low = action should be inhibited.
    /// </summary>
    public float Permission => _integration.Permission;

    /// <summary>
    /// Viability of current direction [-1, 1].
    /// Positive = direction is supported by both frames.
    /// Negative = direction is contradicted.
    /// </summary>
    public float DirectionViability => _integration.DirectionViability;

    /// <summary>
    /// Strain on the transformation itself [0, 1].
    /// High = mapping is under load, frames are diverging.
    /// </summary>
    public float TransformationStrain => _integration.TransformationStrain;

    /// <summary>
    /// Trend of coherence (derivative).
    /// Positive = improving synchronization.
    /// Negative = degrading synchronization.
    /// </summary>
    public float CoherenceTrend => _integration.CoherenceTrend;

    /// <summary>
    /// Update local frame state (right brain input).
    /// </summary>
    public void UpdateLocalFrame(
        float perceptionStrain,      // Z₀ - raw perception load
        float feelingValence,        // Interoceptive feeling [-1, 1]
        float salienceLevel,         // How "loud" current perception is
        float immediacy,             // How urgent is response needed [0, 1]
        float gapPressure)           // Expectation gap pressure
    {
        _local = new LocalFrameState
        {
            PerceptionStrain = perceptionStrain,
            FeelingValence = feelingValence,
            Salience = salienceLevel,
            Immediacy = immediacy,
            GapPressure = gapPressure,
            Timestamp = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Update global frame state (left brain input).
    /// </summary>
    public void UpdateGlobalFrame(
        float predictionConfidence,  // How confident is the prediction
        float patternMatch,          // How well does current match known patterns
        float temporalStability,     // How stable is the prediction over time
        float compressionQuality,    // How well does the model compress reality
        float inheritedLoad)         // Load from inherited references
    {
        _global = new GlobalFrameState
        {
            PredictionConfidence = predictionConfidence,
            PatternMatch = patternMatch,
            TemporalStability = temporalStability,
            CompressionQuality = compressionQuality,
            InheritedLoad = inheritedLoad,
            Timestamp = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Input directional bias (intent vector).
    /// Direction is NOT decided by the center - it's INPUT to the center.
    /// The center tests whether that direction is viable.
    /// </summary>
    public void SetDirectionalBias(
        float magnitude,             // How strong is the directional push [0, 1]
        float alignment,             // How aligned with current posture [-1, 1]
        float novelty)               // How novel is this direction [0, 1]
    {
        _bias = new DirectionalBias
        {
            Magnitude = Math.Clamp(magnitude, 0f, 1f),
            Alignment = Math.Clamp(alignment, -1f, 1f),
            Novelty = Math.Clamp(novelty, 0f, 1f)
        };
    }

    /// <summary>
    /// Integrate frames and compute output.
    /// Call every frame.
    /// </summary>
    public IntegrationState Integrate()
    {
        // 1. Compute frame agreement (are local and global seeing the same thing?)
        float agreement = ComputeFrameAgreement();

        // 2. Compute transformation strain (how hard is it to map between frames?)
        float strain = ComputeTransformationStrain();

        // 3. Compute direction viability (is the proposed direction viable?)
        float viability = ComputeDirectionViability(agreement, strain);

        // 4. Compute permission (should action be allowed?)
        float permission = ComputePermission(agreement, strain, viability);

        // 5. Compute coherence (overall integration quality)
        float coherence = ComputeCoherence(agreement, strain, permission);

        // 6. Update trend
        _coherenceHistory.Enqueue(coherence);
        if (_coherenceHistory.Count > MaxHistory)
            _coherenceHistory.Dequeue();
        float trend = ComputeTrend();

        // 7. Build output
        _integration = new IntegrationState
        {
            Coherence = coherence,
            Permission = permission,
            DirectionViability = viability,
            TransformationStrain = strain,
            CoherenceTrend = trend,
            LocalModulation = ComputeLocalModulation(coherence, strain),
            GlobalModulation = ComputeGlobalModulation(coherence, strain),
            Timestamp = DateTime.UtcNow
        };

        return _integration;
    }

    /// <summary>
    /// Frame agreement: do local and global see the same reality?
    /// </summary>
    private float ComputeFrameAgreement()
    {
        // Local says "I feel X"
        // Global says "Pattern predicts Y"
        // Agreement = how close are X and Y?

        float localSignal = _local.FeelingValence * _local.Salience;
        float globalSignal = _global.PredictionConfidence * _global.PatternMatch;

        // Agreement when both positive or both negative
        float signAgreement = Math.Sign(localSignal) == Math.Sign(globalSignal) ? 1f : 0f;

        // Magnitude agreement (both strong or both weak)
        float magAgreement = 1f - Math.Abs(Math.Abs(localSignal) - Math.Abs(globalSignal));

        // Gap pressure reduces apparent agreement (something is missing)
        float gapPenalty = _local.GapPressure * 0.5f;

        return Math.Clamp(
            signAgreement * 0.6f + magAgreement * 0.4f - gapPenalty,
            0f, 1f);
    }

    /// <summary>
    /// Transformation strain: how hard is the mapping between frames?
    /// </summary>
    private float ComputeTransformationStrain()
    {
        // High strain when:
        // - Local is overwhelmed (perception strain high)
        // - Global can't compress (compression quality low)
        // - Frames are stale (timestamps diverge)
        // - Gaps are present (missing information)

        float perceptionLoad = _local.PerceptionStrain;
        float compressionLoad = 1f - _global.CompressionQuality;
        float staleness = ComputeStaleness();
        float gapLoad = _local.GapPressure;

        return Math.Clamp(
            perceptionLoad * 0.3f +
            compressionLoad * 0.25f +
            staleness * 0.15f +
            gapLoad * 0.3f,
            0f, 1f);
    }

    /// <summary>
    /// Direction viability: is the proposed direction physically possible?
    /// </summary>
    private float ComputeDirectionViability(float agreement, float strain)
    {
        if (_bias.Magnitude < 0.01f)
            return 0f; // No direction proposed

        // Direction is viable when:
        // - Frames agree (both support the direction)
        // - Strain is low (transformation is stable)
        // - Alignment is positive (direction matches posture)
        // - Novelty is manageable (not too far from known)

        float agreementSupport = agreement;
        float strainPenalty = strain;
        float alignmentBonus = (_bias.Alignment + 1f) / 2f; // Map [-1,1] to [0,1]
        float noveltyPenalty = _bias.Novelty * strain; // Novelty is risky under strain

        return Math.Clamp(
            agreementSupport * 0.4f +
            alignmentBonus * 0.3f -
            strainPenalty * 0.2f -
            noveltyPenalty * 0.1f,
            -1f, 1f);
    }

    /// <summary>
    /// Permission: should action be allowed right now?
    /// </summary>
    private float ComputePermission(float agreement, float strain, float viability)
    {
        // Permission is high when:
        // - Agreement is high (frames synchronized)
        // - Strain is low (transformation stable)
        // - Viability is positive (direction supported)
        // - Immediacy doesn't override everything

        float basePermission = agreement * (1f - strain);

        // Viability modulates permission
        if (viability > 0)
            basePermission *= (1f + viability * 0.3f);
        else
            basePermission *= (1f + viability * 0.5f); // Negative viability reduces more

        // High immediacy can force partial permission (survival override)
        if (_local.Immediacy > 0.8f)
            basePermission = Math.Max(basePermission, _local.Immediacy * 0.5f);

        // Gap pressure inhibits permission (uncertainty)
        basePermission *= (1f - _local.GapPressure * 0.4f);

        return Math.Clamp(basePermission, 0f, 1f);
    }

    /// <summary>
    /// Coherence: overall quality of the integration.
    /// </summary>
    private static float ComputeCoherence(float agreement, float strain, float permission)
    {
        // Coherence is the transformation quality metric
        // High coherence = frames are well-synchronized

        return Math.Clamp(
            agreement * 0.4f +
            (1f - strain) * 0.35f +
            permission * 0.25f,
            0f, 1f);
    }

    /// <summary>
    /// Compute coherence trend from history.
    /// </summary>
    private float ComputeTrend()
    {
        if (_coherenceHistory.Count < 3)
            return 0f;

        var list = _coherenceHistory.ToList();
        int mid = list.Count / 2;

        float recentAvg = list.Skip(mid).Average();
        float oldAvg = list.Take(mid).Average();

        return Math.Clamp(recentAvg - oldAvg, -1f, 1f);
    }

    /// <summary>
    /// Staleness: how out of sync are the frame timestamps?
    /// </summary>
    private float ComputeStaleness()
    {
        var diff = Math.Abs((_local.Timestamp - _global.Timestamp).TotalMilliseconds);
        // Over 100ms is stale
        return Math.Min(1f, (float)(diff / 100.0));
    }

    /// <summary>
    /// Compute modulation signal for local frame (right brain).
    /// </summary>
    private static float ComputeLocalModulation(float coherence, float strain)
    {
        // Under low coherence: tell local to be more conservative
        // Under high strain: tell local to reduce load
        return Math.Clamp(
            coherence * 0.6f + (1f - strain) * 0.4f,
            0f, 1f);
    }

    /// <summary>
    /// Compute modulation signal for global frame (left brain).
    /// </summary>
    private static float ComputeGlobalModulation(float coherence, float strain)
    {
        // Under low coherence: tell global to widen patterns
        // Under high strain: tell global to simplify
        return Math.Clamp(
            coherence * 0.5f + (1f - strain) * 0.5f,
            0f, 1f);
    }

    /// <summary>
    /// Get current integration state.
    /// </summary>
    public IntegrationState GetState() => _integration;

    /// <summary>
    /// Get diagnostic summary.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"""
            === FRAME INTEGRATOR (CENTER) ===
            Coherence: {Coherence:F2} (trend: {CoherenceTrend:+0.00;-0.00})
            Permission: {Permission:F2}
            Direction Viability: {DirectionViability:+0.00;-0.00}
            Transformation Strain: {TransformationStrain:F2}

            Local Frame:
              Perception: {_local.PerceptionStrain:F2}
              Feeling: {_local.FeelingValence:+0.00;-0.00}
              Salience: {_local.Salience:F2}
              Immediacy: {_local.Immediacy:F2}
              Gap Pressure: {_local.GapPressure:F2}

            Global Frame:
              Prediction: {_global.PredictionConfidence:F2}
              Pattern Match: {_global.PatternMatch:F2}
              Stability: {_global.TemporalStability:F2}
              Compression: {_global.CompressionQuality:F2}

            Modulation → Local: {_integration.LocalModulation:F2}
            Modulation → Global: {_integration.GlobalModulation:F2}
            =================================
            """;
    }
}

/// <summary>
/// Local frame state (right brain - perception/feeling/immediate).
/// </summary>
public struct LocalFrameState
{
    public float PerceptionStrain;
    public float FeelingValence;
    public float Salience;
    public float Immediacy;
    public float GapPressure;
    public DateTime Timestamp;
}

/// <summary>
/// Global frame state (left brain - prediction/pattern/anticipatory).
/// </summary>
public struct GlobalFrameState
{
    public float PredictionConfidence;
    public float PatternMatch;
    public float TemporalStability;
    public float CompressionQuality;
    public float InheritedLoad;
    public DateTime Timestamp;
}

/// <summary>
/// Directional bias (intent vector input to the center).
/// </summary>
public struct DirectionalBias
{
    public float Magnitude;
    public float Alignment;
    public float Novelty;
}

/// <summary>
/// Integration output (what the center produces).
/// </summary>
public struct IntegrationState
{
    /// <summary>Overall coherence of frame mapping [0, 1].</summary>
    public float Coherence;

    /// <summary>Permission level for action [0, 1].</summary>
    public float Permission;

    /// <summary>Viability of current direction [-1, 1].</summary>
    public float DirectionViability;

    /// <summary>Strain on the transformation [0, 1].</summary>
    public float TransformationStrain;

    /// <summary>Trend of coherence (derivative).</summary>
    public float CoherenceTrend;

    /// <summary>Modulation signal to local frame (right brain).</summary>
    public float LocalModulation;

    /// <summary>Modulation signal to global frame (left brain).</summary>
    public float GlobalModulation;

    /// <summary>When this state was computed.</summary>
    public DateTime Timestamp;
}
