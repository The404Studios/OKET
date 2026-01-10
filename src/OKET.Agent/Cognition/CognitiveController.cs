using OKET.Core.Types;
using OKET.Core.State;
using OKET.Core.Audio;
using OKET.Core.Actions;
using OKET.Core.Cognition;
using OKET.Core.Interfaces;
using OKET.Agent.Fusion;
using OKET.Agent.Decision;

namespace OKET.Agent.Cognition;

/// <summary>
/// Central cognitive controller that orchestrates:
/// - Multimodal fusion (vision + audio → belief)
/// - Z-score stack (multi-layer normalization)
/// - Interoceptive processing (feeling layer)
/// - Decision modulation based on system state
///
/// This implements the full cognitive architecture:
///   Perception → Belief → Feeling → Decision
///
/// Where feeling modulates both perception and decision.
/// </summary>
public sealed class CognitiveController
{
    private readonly MultimodalFusion _fusion = new();
    private readonly InteroceptiveProcessor _interoception = new();
    private readonly ZScoreStack _zScores = new();
    private readonly ZScoreComputer _zScoreComputer = new();

    private readonly IPolicy _policy;
    private readonly SkillExecutor _skillExecutor;

    // Current cognitive state
    private BeliefState? _currentBelief;
    private InteroceptiveState? _currentFeeling;
    private ActionPlan? _lastPlan;

    public BeliefState? CurrentBelief => _currentBelief;
    public InteroceptiveState? CurrentFeeling => _currentFeeling;
    public ZScoreStack ZScores => _zScores;

    public CognitiveController(IPolicy policy, SkillExecutor skillExecutor)
    {
        _policy = policy;
        _skillExecutor = skillExecutor;
    }

    /// <summary>
    /// Process one cognitive cycle.
    /// </summary>
    public ActionPlan Process(GameState gameState, AudioSnapshot audioSnapshot)
    {
        // 1. Update Z-score stack (multi-layer normalization)
        var zScoreInputs = _zScoreComputer.ComputeInputs(gameState, audioSnapshot, _lastPlan);
        _zScores.Update(zScoreInputs);

        // 2. Multimodal fusion: vision + audio + HUD → belief
        _currentBelief = _fusion.Fuse(gameState, audioSnapshot);

        // 3. Interoceptive processing: compute feeling state
        _currentFeeling = _interoception.Process(gameState, _currentBelief, _lastPlan, _zScores);

        // 4. Modulate perception/belief based on feeling
        var modulatedBelief = ModulateBelief(_currentBelief, _currentFeeling);

        // 5. Decision: choose strategic mode (modulated by feeling)
        var (mode, baseConfidence) = _policy.Decide(gameState);
        var modulatedConfidence = ModulateDecisionConfidence(baseConfidence, _currentFeeling);

        // 6. Check if we should hesitate or force action
        if (_currentFeeling.ShouldHesitate && mode != StrategicMode.Kite && mode != StrategicMode.Unstick)
        {
            // Low stability - be more defensive
            if (modulatedBelief.ThreatLevel > 0.3f)
            {
                mode = StrategicMode.Kite;
            }
        }
        else if (_currentFeeling.MustActNow && mode == StrategicMode.Idle)
        {
            // High urgency - can't stay idle
            mode = modulatedBelief.ThreatLevel > 0.5f ? StrategicMode.Kite : StrategicMode.Fight;
        }

        // 7. Execute skill for chosen mode
        var plan = _skillExecutor.Execute(gameState, mode);

        // 8. Modulate action speed based on feeling
        plan = ModulateActionPlan(plan, _currentFeeling);

        _lastPlan = plan;
        return plan;
    }

    private BeliefState ModulateBelief(BeliefState belief, InteroceptiveState feeling)
    {
        // When perception trust is low, reduce belief confidence
        float trustModifier = feeling.PerceptionTrust;

        // When sensory alignment is low, be more conservative about threats
        float threatModifier = feeling.SensoryAlignment;

        return belief with
        {
            Confidence = belief.Confidence * trustModifier,
            ThreatLevel = belief.ThreatLevel * (0.5f + threatModifier * 0.5f),
            // Hits are still reliable if audio confirmed
            HitConfidence = belief.HitConfidence * Math.Max(trustModifier, belief.AudioContribution)
        };
    }

    private float ModulateDecisionConfidence(float baseConfidence, InteroceptiveState feeling)
    {
        // Commitment confidence affects decision confidence
        return baseConfidence * feeling.CommitmentConfidence;
    }

    private ActionPlan ModulateActionPlan(ActionPlan plan, InteroceptiveState feeling)
    {
        // Action speed modifier affects how aggressive we are
        float speedMod = feeling.ActionSpeedModifier;

        // If high anxiety, add more defensive actions
        if (feeling.Anxiety > 0.6f && plan.Mode == StrategicMode.Fight)
        {
            // Add strafe movements to fight plan
            var actions = plan.Actions.ToList();
            if (!actions.Any(a => a.Type is ActionType.MoveLeft or ActionType.MoveRight))
            {
                actions.Add(GameAction.Press(
                    Random.Shared.Next(2) == 0 ? ActionType.MoveLeft : ActionType.MoveRight,
                    (int)(100 * speedMod)));
            }

            return plan with
            {
                Actions = actions,
                Reason = plan.Reason + " [anxious: added strafe]"
            };
        }

        // If high frustration, consider mode change
        if (feeling.Frustration > 0.7f)
        {
            return plan with
            {
                Reason = plan.Reason + " [frustrated: may need strategy change]",
                Confidence = plan.Confidence * 0.8f
            };
        }

        // If highly focused, increase confidence
        if (feeling.Focus > 0.7f)
        {
            return plan with
            {
                Reason = plan.Reason + " [focused]",
                Confidence = Math.Min(plan.Confidence * 1.2f, 1f)
            };
        }

        return plan;
    }

    /// <summary>
    /// Get diagnostic information about current cognitive state.
    /// </summary>
    public string GetDiagnostics()
    {
        var beliefInfo = _currentBelief != null
            ? $"Belief: threat={_currentBelief.ThreatLevel:F2}, conf={_currentBelief.Confidence:F2}, agree={_currentBelief.SensoryAgreement:F2}"
            : "Belief: none";

        var feelingInfo = _currentFeeling?.GetSummary() ?? "Feeling: none";

        var zInfo = _zScores.GetDiagnostics();

        return $"""
            === COGNITIVE STATE ===
            {beliefInfo}
            {feelingInfo}
            {zInfo}
            =======================
            """;
    }
}

/// <summary>
/// Computes Z-score inputs from raw observations.
/// </summary>
public sealed class ZScoreComputer
{
    private readonly WindowedStatistics _motionStats = new(100);
    private readonly WindowedStatistics _brightnessStats = new(100);
    private readonly WindowedStatistics _threatCountStats = new(100);
    private readonly WindowedStatistics _audioLevelStats = new(100);
    private readonly WindowedStatistics _audioEventStats = new(100);
    private readonly WindowedStatistics _healthChangeStats = new(100);
    private readonly WindowedStatistics _agreementStats = new(100);
    private readonly WindowedStatistics _volatilityStats = new(100);
    private readonly WindowedStatistics _controlStats = new(100);

    private int _lastHealth = 100;
    private int _lastAmmo = 30;
    private int _lastThreatCount;
    private StrategicMode _lastMode;
    private float _modeChangesRecent;
    private int _shotsFired;
    private int _hitsRecent;

    public ZScoreInputs ComputeInputs(GameState state, AudioSnapshot audio, ActionPlan? lastPlan)
    {
        // Update statistics
        _motionStats.Add(state.Detections.ThreatCount > 0 ? 1 : 0);
        _threatCountStats.Add(state.Detections.ThreatCount);
        _audioLevelStats.Add(audio.AverageLevel);
        _audioEventStats.Add(audio.Events.Count);

        int healthChange = state.Hud.Health - _lastHealth;
        _healthChangeStats.Add(healthChange);
        _lastHealth = state.Hud.Health;

        int ammoChange = state.Hud.AmmoClip - _lastAmmo;
        _lastAmmo = state.Hud.AmmoClip;

        // Mode change tracking
        if (lastPlan != null && lastPlan.Mode != _lastMode)
        {
            _modeChangesRecent++;
            _lastMode = lastPlan.Mode;
        }
        _modeChangesRecent *= 0.95f; // Decay

        // Control tracking
        if (lastPlan != null && lastPlan.Actions.Any(a => a.Type == ActionType.Attack))
        {
            _shotsFired++;
        }
        if (state.Aim.HitConfirmed)
        {
            _hitsRecent++;
        }
        _hitsRecent = (int)(_hitsRecent * 0.95f);
        _shotsFired = Math.Max(1, (int)(_shotsFired * 0.95f));

        float controlScore = _hitsRecent / (float)_shotsFired;
        _controlStats.Add(controlScore);

        _lastThreatCount = state.Detections.ThreatCount;

        return new ZScoreInputs
        {
            VisionStats = new VisionStatistics
            {
                MotionZScore = (float)_motionStats.ZScore(state.Detections.ThreatCount > 0 ? 1 : 0),
                BrightnessZScore = 0, // Would need brightness calculation
                ThreatCountZScore = (float)_threatCountStats.ZScore(state.Detections.ThreatCount)
            },
            AudioStats = new AudioStatistics
            {
                LevelZScore = (float)_audioLevelStats.ZScore(audio.AverageLevel),
                EventRateZScore = (float)_audioEventStats.ZScore(audio.Events.Count),
                ThreatSoundZScore = audio.HasThreatSounds ? 1f : 0f,
                DamageSoundDetected = audio.HasDamageSounds
            },
            HudStats = new HudStatistics
            {
                HealthChangeZScore = (float)_healthChangeStats.ZScore(healthChange),
                AmmoChangeZScore = ammoChange < 0 ? -1f : 0f
            },
            CorrelationStats = new CorrelationStatistics
            {
                FiringAudioCorrelation = audio.HasCombatSounds && lastPlan?.Mode == StrategicMode.Fight ? 1f : 0.5f,
                MovementPositionCorrelation = state.IsStuck ? 0f : 1f
            },
            BeliefStats = new BeliefStatistics
            {
                ModeChangesPerSecond = _modeChangesRecent,
                TargetSwitchesPerSecond = 0, // Would track target changes
                ConfidenceVariance = 0.1f
            },
            ControlStats = new ControlStatistics
            {
                ShotsFired = _shotsFired,
                HitsConfirmed = _hitsRecent,
                ExpectedAccuracy = 0.5f,
                MovementProducingChange = !state.IsStuck,
                HealthTrend = healthChange / 10f
            },
            PerceptualAgreementBaseline = (float)_agreementStats.Mean,
            PerceptualAgreementStdDev = (float)Math.Max(_agreementStats.StdDev, 0.1),
            BeliefVolatilityBaseline = (float)_volatilityStats.Mean,
            BeliefVolatilityStdDev = (float)Math.Max(_volatilityStats.StdDev, 0.1),
            ControlEfficacyBaseline = (float)_controlStats.Mean,
            ControlEfficacyStdDev = (float)Math.Max(_controlStats.StdDev, 0.1)
        };
    }
}
