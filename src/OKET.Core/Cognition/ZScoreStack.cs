namespace OKET.Core.Cognition;

/// <summary>
/// Multi-layered Z-score normalization stack.
/// Each layer normalizes a different domain at a different timescale.
///
/// Z₀ - Sensory: Is this signal unusual relative to recent baseline?
/// Z₁ - Perceptual: Do my senses agree more or less than usual?
/// Z₂ - Belief: Is my current belief unusually unstable?
/// Z₃ - Control: Are my actions working less than expected?
/// Z₄ - Coherence: Is the system drifting out of alignment?
/// </summary>
public sealed class ZScoreStack
{
    /// <summary>Z₀: Sensory normalization scores per modality.</summary>
    public SensoryZScores Z0 { get; private set; } = new();

    /// <summary>Z₁: Cross-modal agreement score.</summary>
    public float Z1_PerceptualAgreement { get; private set; }

    /// <summary>Z₂: Belief stability score.</summary>
    public float Z2_BeliefStability { get; private set; }

    /// <summary>Z₃: Control efficacy score.</summary>
    public float Z3_ControlEfficacy { get; private set; }

    /// <summary>Z₄: Global coherence score.</summary>
    public float Z4_GlobalCoherence { get; private set; }

    /// <summary>Overall system strain (composite of all Z-scores).</summary>
    public float SystemStrain { get; private set; }

    /// <summary>Whether the system is in a stable state.</summary>
    public bool IsStable => SystemStrain < 1.5f;

    /// <summary>Whether the system is under severe strain.</summary>
    public bool IsStrained => SystemStrain > 2.0f;

    public void Update(ZScoreInputs inputs)
    {
        // Update Z₀: Sensory normalization
        Z0 = UpdateSensoryZScores(inputs);

        // Update Z₁: Perceptual agreement
        Z1_PerceptualAgreement = UpdatePerceptualAgreement(inputs, Z0);

        // Update Z₂: Belief stability
        Z2_BeliefStability = UpdateBeliefStability(inputs);

        // Update Z₃: Control efficacy
        Z3_ControlEfficacy = UpdateControlEfficacy(inputs);

        // Update Z₄: Global coherence
        Z4_GlobalCoherence = UpdateGlobalCoherence();

        // Calculate overall strain
        SystemStrain = CalculateSystemStrain();
    }

    private static SensoryZScores UpdateSensoryZScores(ZScoreInputs inputs)
    {
        return new SensoryZScores
        {
            Vision_Motion = inputs.VisionStats.MotionZScore,
            Vision_Brightness = inputs.VisionStats.BrightnessZScore,
            Vision_ThreatCount = inputs.VisionStats.ThreatCountZScore,
            Audio_Level = inputs.AudioStats.LevelZScore,
            Audio_EventRate = inputs.AudioStats.EventRateZScore,
            Audio_ThreatSounds = inputs.AudioStats.ThreatSoundZScore,
            Hud_HealthChange = inputs.HudStats.HealthChangeZScore,
            Hud_AmmoChange = inputs.HudStats.AmmoChangeZScore
        };
    }

    private static float UpdatePerceptualAgreement(ZScoreInputs inputs, SensoryZScores z0)
    {
        // Measure agreement between vision and audio

        // Vision says threat, audio agrees?
        float visionThreat = Math.Clamp(z0.Vision_ThreatCount, 0, 3);
        float audioThreat = Math.Clamp(z0.Audio_ThreatSounds, 0, 3);

        // Agreement when both high or both low
        float threatAgreement = 1f - Math.Abs(visionThreat - audioThreat) / 3f;

        // HUD and audio agreement on damage
        float hudDamage = Math.Clamp(z0.Hud_HealthChange, -3, 0);
        float audioDamage = inputs.AudioStats.DamageSoundDetected ? -2f : 0f;
        float damageAgreement = 1f - Math.Abs(hudDamage - audioDamage) / 3f;

        // Combat sounds should correlate with firing
        float combatAgreement = inputs.CorrelationStats.FiringAudioCorrelation;

        // Weighted average
        float agreement = (threatAgreement * 0.4f + damageAgreement * 0.3f + combatAgreement * 0.3f);

        // Return Z-score: how unusual is this agreement level?
        return (agreement - inputs.PerceptualAgreementBaseline) / Math.Max(inputs.PerceptualAgreementStdDev, 0.1f);
    }

    private static float UpdateBeliefStability(ZScoreInputs inputs)
    {
        // How much are beliefs changing?
        float beliefFlips = inputs.BeliefStats.ModeChangesPerSecond;
        float targetSwitches = inputs.BeliefStats.TargetSwitchesPerSecond;
        float confidenceVariance = inputs.BeliefStats.ConfidenceVariance;

        // Higher volatility = higher Z-score (more unstable)
        float volatility = beliefFlips * 0.4f + targetSwitches * 0.3f + confidenceVariance * 0.3f;

        return (volatility - inputs.BeliefVolatilityBaseline) / Math.Max(inputs.BeliefVolatilityStdDev, 0.1f);
    }

    private static float UpdateControlEfficacy(ZScoreInputs inputs)
    {
        // Are actions producing expected outcomes?
        float expectedHits = inputs.ControlStats.ShotsFired * inputs.ControlStats.ExpectedAccuracy;
        float actualHits = inputs.ControlStats.HitsConfirmed;
        float hitRatio = expectedHits > 0 ? actualHits / expectedHits : 1f;

        float movementEffectiveness = inputs.ControlStats.MovementProducingChange ? 1f : 0f;
        float survivalTrend = inputs.ControlStats.HealthTrend; // Positive = improving

        // Control score: higher = better
        float control = (hitRatio * 0.4f + movementEffectiveness * 0.3f + (survivalTrend + 1) / 2 * 0.3f);

        // Return Z-score: negative means worse than expected
        return (control - inputs.ControlEfficacyBaseline) / Math.Max(inputs.ControlEfficacyStdDev, 0.1f);
    }

    private float UpdateGlobalCoherence()
    {
        // Z₄ is a function of all other Z-scores
        // It measures whether the system as a whole is making sense

        // High Z₁ (agreement) + stable Z₂ (beliefs) + effective Z₃ (control) = coherent
        // Low agreement + unstable beliefs + failing control = incoherent

        float agreementContribution = Math.Clamp(Z1_PerceptualAgreement, -2, 2);
        float stabilityContribution = Math.Clamp(-Z2_BeliefStability, -2, 2); // Inverted: low volatility is good
        float controlContribution = Math.Clamp(Z3_ControlEfficacy, -2, 2);

        // Sensory extremes contribute to incoherence
        float sensoryExtreme = (Math.Abs(Z0.Vision_Motion) + Math.Abs(Z0.Audio_Level)) / 2;
        float sensoryContribution = Math.Clamp(2 - sensoryExtreme, -2, 2);

        // Weighted combination
        float coherence = (
            agreementContribution * 0.3f +
            stabilityContribution * 0.25f +
            controlContribution * 0.25f +
            sensoryContribution * 0.2f
        );

        return coherence;
    }

    private float CalculateSystemStrain()
    {
        // Strain is the magnitude of deviation from baseline across all layers
        // High strain = the system is working hard to maintain coherence

        float z0Strain = (
            Math.Abs(Z0.Vision_Motion) +
            Math.Abs(Z0.Vision_ThreatCount) +
            Math.Abs(Z0.Audio_Level) +
            Math.Abs(Z0.Audio_ThreatSounds) +
            Math.Abs(Z0.Hud_HealthChange)
        ) / 5f;

        float z1Strain = Math.Abs(Z1_PerceptualAgreement);
        float z2Strain = Math.Abs(Z2_BeliefStability);
        float z3Strain = Math.Abs(Z3_ControlEfficacy);
        float z4Strain = Math.Abs(Z4_GlobalCoherence);

        // Weighted: higher layers contribute more to strain
        return z0Strain * 0.15f + z1Strain * 0.2f + z2Strain * 0.2f + z3Strain * 0.2f + z4Strain * 0.25f;
    }

    /// <summary>
    /// Get a diagnostic summary of the Z-score stack.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"""
            Z-Score Stack:
              Z₀ Vision: motion={Z0.Vision_Motion:F2}, threats={Z0.Vision_ThreatCount:F2}
              Z₀ Audio:  level={Z0.Audio_Level:F2}, threats={Z0.Audio_ThreatSounds:F2}
              Z₀ HUD:    health={Z0.Hud_HealthChange:F2}
              Z₁ Agreement: {Z1_PerceptualAgreement:F2}
              Z₂ Stability: {Z2_BeliefStability:F2}
              Z₃ Control:   {Z3_ControlEfficacy:F2}
              Z₄ Coherence: {Z4_GlobalCoherence:F2}
              System Strain: {SystemStrain:F2} ({(IsStable ? "stable" : IsStrained ? "STRAINED" : "elevated")})
            """;
    }
}

/// <summary>
/// Z₀ sensory normalization scores per modality.
/// </summary>
public sealed class SensoryZScores
{
    // Vision
    public float Vision_Motion { get; init; }
    public float Vision_Brightness { get; init; }
    public float Vision_ThreatCount { get; init; }

    // Audio
    public float Audio_Level { get; init; }
    public float Audio_EventRate { get; init; }
    public float Audio_ThreatSounds { get; init; }

    // HUD
    public float Hud_HealthChange { get; init; }
    public float Hud_AmmoChange { get; init; }
}

/// <summary>
/// Inputs required to compute Z-scores.
/// </summary>
public sealed class ZScoreInputs
{
    // Sensory statistics
    public VisionStatistics VisionStats { get; init; } = new();
    public AudioStatistics AudioStats { get; init; } = new();
    public HudStatistics HudStats { get; init; } = new();
    public CorrelationStatistics CorrelationStats { get; init; } = new();

    // Belief statistics
    public BeliefStatistics BeliefStats { get; init; } = new();

    // Control statistics
    public ControlStatistics ControlStats { get; init; } = new();

    // Baselines (running averages)
    public float PerceptualAgreementBaseline { get; init; } = 0.7f;
    public float PerceptualAgreementStdDev { get; init; } = 0.2f;
    public float BeliefVolatilityBaseline { get; init; } = 0.3f;
    public float BeliefVolatilityStdDev { get; init; } = 0.2f;
    public float ControlEfficacyBaseline { get; init; } = 0.6f;
    public float ControlEfficacyStdDev { get; init; } = 0.2f;
}

public sealed class VisionStatistics
{
    public float MotionZScore { get; init; }
    public float BrightnessZScore { get; init; }
    public float ThreatCountZScore { get; init; }
}

public sealed class AudioStatistics
{
    public float LevelZScore { get; init; }
    public float EventRateZScore { get; init; }
    public float ThreatSoundZScore { get; init; }
    public bool DamageSoundDetected { get; init; }
}

public sealed class HudStatistics
{
    public float HealthChangeZScore { get; init; }
    public float AmmoChangeZScore { get; init; }
}

public sealed class CorrelationStatistics
{
    public float FiringAudioCorrelation { get; init; }
    public float MovementPositionCorrelation { get; init; }
}

public sealed class BeliefStatistics
{
    public float ModeChangesPerSecond { get; init; }
    public float TargetSwitchesPerSecond { get; init; }
    public float ConfidenceVariance { get; init; }
}

public sealed class ControlStatistics
{
    public int ShotsFired { get; init; }
    public int HitsConfirmed { get; init; }
    public float ExpectedAccuracy { get; init; }
    public bool MovementProducingChange { get; init; }
    public float HealthTrend { get; init; }
}
