using OKET.Core.Types;
using OKET.Core.State;
using OKET.Core.Audio;
using OKET.Core.Actions;
using OKET.Core.Cognition;
using OKET.Core.Interfaces;
using OKET.Core.Operators;
using OKET.Core.Integration;
using OKET.Agent.Fusion;
using OKET.Agent.Decision;
using OKET.Agent.Memory;

namespace OKET.Agent.Cognition;

/// <summary>
/// Central cognitive controller with correct data flow:
///
///   RAW PERCEPTION
///     Vision + Audio + HUD
///           ↓
///   Z₀: Sensory normalization (per modality)
///           ↓
///   Z₁: Cross-modal agreement
///           ↓
///   MULTIMODAL FUSION → BeliefState(t)
///           ↓
///   Z₂: Belief volatility     Z₃: Control efficacy
///         \                 /
///          \               /
///           ↓             ↓
///         Z₄: Global strain (SystemStrain)
///                ↓
///   INTEROCEPTION / FEELING
///     inputs: Z₁,Z₂,Z₃,Z₄ + outcome trend + prediction error
///     outputs: Trust, Urgency, Hesitation, LearningRate, ActionSpeed
///                ↓
///   BELIEF LOCK + MODE HYSTERESIS  ← prevents thrashing
///                ↓
///   DECISION (Committed + Feeling + Strain)
///     Policy → Skills → Safety → Actuator
/// </summary>
public sealed class CognitiveController
{
    private readonly MultimodalFusion _fusion = new();
    private readonly InteroceptiveProcessor _interoception = new();
    private readonly ZScoreStack _zScores = new();
    private readonly ZScoreComputer _zScoreComputer = new();
    private readonly BeliefLock _beliefLock = new();

    // Operator algebra components
    private readonly CueRegistry _cueRegistry = CueRegistry.CreateDefault();
    private readonly BindingValidator _bindingValidator = new();

    // Reference memory (operational understanding)
    private readonly ReferenceBuilder _refBuilder = new();

    // Frame Integrator (CENTER - neutral opposite of left/right)
    private readonly FrameIntegrator _frameIntegrator = new();
    private readonly IntegrationBridge _integrationBridge;

    private readonly IPolicy _policy;
    private readonly SkillExecutor _skillExecutor;

    // Current cognitive state
    private BeliefState? _rawBelief;
    private BeliefState? _committedBelief;
    private InteroceptiveState? _currentFeeling;
    private CommittedState? _committedState;
    private ActionPlan? _lastPlan;

    // Operator state
    private BindState _currentBindState = BindState.Separate;
    private GateContext _currentGateContext;
    private float _lastStrainBeforeDiscount;
    private float _lastOutcomeTrend;

    public BeliefState? RawBelief => _rawBelief;
    public BeliefState? CommittedBelief => _committedBelief;
    public InteroceptiveState? CurrentFeeling => _currentFeeling;
    public CommittedState? CurrentCommitment => _committedState;
    public ZScoreStack ZScores => _zScores;
    public BeliefLock BeliefLock => _beliefLock;
    public CueRegistry CueRegistry => _cueRegistry;
    public BindState CurrentBindState => _currentBindState;
    public GateContext CurrentGateContext => _currentGateContext;
    public ReferenceBuilder RefMemory => _refBuilder;
    public float ExpectationGapPressure => _refBuilder.Gaps.TotalGapPressure;
    public FrameIntegrator FrameIntegrator => _frameIntegrator;
    public float CenterCoherence => _frameIntegrator.Coherence;
    public float CenterPermission => _frameIntegrator.Permission;

    public CognitiveController(IPolicy policy, SkillExecutor skillExecutor)
    {
        _policy = policy;
        _skillExecutor = skillExecutor;
        _integrationBridge = new IntegrationBridge(_frameIntegrator, _zScores);
    }

    /// <summary>
    /// Process one cognitive cycle with correct data flow.
    /// Integrates operator algebra for validated information flow.
    /// </summary>
    public ActionPlan Process(GameState gameState, AudioSnapshot audioSnapshot)
    {
        // Track previous state for credit assignment
        float prevStrain = _zScores.SystemStrain;
        float prevOutcome = _currentFeeling?.OutcomeTrend ?? 0f;

        // === STAGE 1: Z-SCORE STACK (Z₀ → Z₁ → Z₂/Z₃ → Z₄) ===
        var zScoreInputs = _zScoreComputer.ComputeInputs(gameState, audioSnapshot, _lastPlan);
        _zScores.Update(zScoreInputs);

        // === STAGE 1.5: REFERENCE MEMORY - PERCEPTION ===
        // Build references from what "thinking heard and saw"
        _refBuilder.AfterPerception(gameState, audioSnapshot, _zScores);

        // === STAGE 2: MULTIMODAL FUSION ===
        // Uses Z₀, Z₁ implicitly through the fusion algorithm
        _rawBelief = _fusion.Fuse(gameState, audioSnapshot);

        // === STAGE 3: INTEROCEPTION (takes Z₄ as INPUT) ===
        // Z₁-Z₄ feed into feeling, along with outcome trend and prediction error
        _currentFeeling = _interoception.Process(
            gameState,
            _rawBelief,
            _lastPlan,
            _zScores.SystemStrain,           // Z₄ as input
            _zScores.Z1_PerceptualAgreement, // Z₁ as input
            _zScores.Z2_BeliefStability,     // Z₂ as input
            _zScores.Z3_ControlEfficacy);    // Z₃ as input

        // === STAGE 3.25: REFERENCE MEMORY - FUSION ===
        // Build belief candidate references
        _refBuilder.AfterFusion(_rawBelief, _currentFeeling, _zScores);

        // === STAGE 3.4: FRAME INTEGRATION (CENTER) ===
        // The neutral opposite - computes transformation between local and global frames
        // Outputs: Permission, Coherence, Modulation signals to BOTH sides
        float gapPressure = _refBuilder.Gaps.TotalGapPressure;
        var integrationState = _integrationBridge.Update(
            _currentFeeling,
            gapPressure,
            predictionConfidence: _rawBelief.Confidence,
            patternMatch: 1f - _rawBelief.AudioVisualConflict,
            immediacy: _currentFeeling.MustActNow ? 0.9f : _currentFeeling.Urgency);

        // Set directional bias based on current threat assessment
        _integrationBridge.SetDirection(
            magnitude: _rawBelief.ThreatLevel,
            alignment: _currentFeeling.OutcomeTrend,
            novelty: _zScores.Z2_BeliefStability > 1f ? 0.7f : 0.3f);

        // === STAGE 3.5: CUE EVALUATION ===
        // Evaluate cues to get strain discount
        // Cues that reliably predict survival earn the right to reduce strain pressure
        float cueStrainDiscount = _cueRegistry.Evaluate(_zScores, _currentFeeling, _rawBelief);
        _lastStrainBeforeDiscount = _zScores.SystemStrain;

        // === STAGE 4: MODULATE BELIEF BASED ON FEELING ===
        var modulatedBelief = ModulateBelief(_rawBelief, _currentFeeling);

        // === STAGE 4.5: UPDATE BIND STATE ===
        // Track information binding topology
        _currentBindState = DetermineBindState(modulatedBelief, _currentFeeling, _committedState);

        // === STAGE 5: POLICY DECISION (proposed mode) ===
        var (proposedMode, baseConfidence) = _policy.Decide(gameState);

        // Get proposed target
        int? proposedTargetId = gameState.Detections.PrimaryThreat?.TrackId;

        // === STAGE 5.5: BUILD GATE CONTEXT + VALIDATE ===
        // Build context for gate validation
        // Include expectation gap pressure in inhibition check
        // (what we haven't thought about can inhibit emission)
        // Use CENTER permission signal to gate action
        bool isInhibited = (_currentFeeling.ValidityCompromised && _currentFeeling.ShouldHesitate)
                        || (gapPressure > 0.6f && !_currentFeeling.MustActNow)
                        || _integrationBridge.ShouldInhibit();
        _currentGateContext = BindingValidator.BuildContext(
            _currentBindState,
            _currentFeeling.Validity - gapPressure * 0.2f, // Gaps reduce effective validity
            _currentFeeling.PerceptionTrust,
            _zScores.SystemStrain - cueStrainDiscount + gapPressure * 0.3f, // Gaps add to strain
            isInhibited,
            _currentFeeling.OutcomeTrend,
            _currentFeeling.MustActNow);

        // === STAGE 6: BELIEF LOCK / HYSTERESIS GATE ===
        // This prevents thrashing - won't switch unless new state wins by margin for duration
        _committedState = _beliefLock.Process(
            modulatedBelief,
            proposedMode,
            proposedTargetId,
            _currentFeeling);

        _committedBelief = _committedState.Belief;
        var committedMode = _committedState.Mode;

        // === STAGE 6.5: CREDIT ASSIGNMENT ===
        // Record outcome for cues that fired - did this posture survive the sink?
        float strainDelta = _zScores.SystemStrain - prevStrain;
        float outcomeDelta = _currentFeeling.OutcomeTrend - prevOutcome;
        bool survived = !_committedState.ForcedUnlock && _currentFeeling.Validity > 0.35f;
        _cueRegistry.RecordOutcome(survived, strainDelta, outcomeDelta);
        _lastOutcomeTrend = outcomeDelta;

        // === STAGE 6.75: REFERENCE MEMORY - OUTCOME ===
        // Record outcome for reference memory (what happened after acting)
        _refBuilder.AfterOutcome(
            strainDelta,
            outcomeDelta,
            gameState.Aim.HitConfirmed,
            survived);

        // === STAGE 6.9: META-COGNITIVE CLOSURE ===
        // Check what we're thinking against what we haven't thought
        // This is where "absence is information" becomes computable
        _refBuilder.CheckExpectations(
            hasAudioConfirmation: audioSnapshot.HasCombatSounds || audioSnapshot.HasDamageSounds,
            hasVisualConfirmation: gameState.Aim.HitConfirmed,
            modalityAgreement: _zScores.Z1_PerceptualAgreement,
            actionHadFeedback: _lastPlan?.Actions.Count > 0,
            strainTrend: strainDelta,
            confidenceLevel: _rawBelief.Confidence,
            hasTarget: gameState.Detections.PrimaryThreat != null,
            controlHadEffect: _zScores.Z3_ControlEfficacy > 0);

        // === STAGE 7: GATE-VALIDATED OVERRIDES ===
        // Validate actions through operator algebra
        var emitValidation = _bindingValidator.Validate(GateType.Emit, _currentGateContext);
        var yieldValidation = _bindingValidator.Validate(GateType.Yield, _currentGateContext);

        if (!emitValidation.Permitted && yieldValidation.Permitted)
        {
            // Gate says yield - be defensive
            if (committedMode != StrategicMode.Kite && committedMode != StrategicMode.Unstick)
            {
                committedMode = StrategicMode.Kite;
            }
        }
        else if (_currentFeeling.MustActNow && committedMode == StrategicMode.Idle)
        {
            // Urgency override - can't stay idle
            committedMode = _committedBelief.ThreatLevel > 0.5f ? StrategicMode.Kite : StrategicMode.Fight;
            _beliefLock.ForceMode(committedMode);
        }
        else if (_currentFeeling.ShouldHesitate &&
                 committedMode != StrategicMode.Kite &&
                 committedMode != StrategicMode.Unstick &&
                 _committedBelief.ThreatLevel > 0.3f)
        {
            // Hesitation override - be defensive when uncertain
            committedMode = StrategicMode.Kite;
        }

        // === STAGE 8: SKILL EXECUTION ===
        var plan = _skillExecutor.Execute(gameState, committedMode);

        // === STAGE 8.5: REFERENCE MEMORY - COMMITMENT ===
        // Record commitment and action plan in reference memory
        _refBuilder.AfterCommitment(_committedState, plan, _currentFeeling);

        // === STAGE 9: ACTION MODULATION ===
        plan = ModulateActionPlan(plan, _currentFeeling);

        _lastPlan = plan;
        return plan;
    }

    /// <summary>
    /// Determine bind state based on cognitive signals.
    /// </summary>
    private BindState DetermineBindState(
        BeliefState belief,
        InteroceptiveState feeling,
        CommittedState? commitment)
    {
        // Absent: validity compromised + inhibited
        if (feeling.ValidityCompromised && feeling.ShouldHesitate)
            return BindState.Absent;

        // Inherited: committed, locked, high validity
        if (commitment?.IsLocked == true &&
            commitment.FramesSinceCommit > 10 &&
            feeling.Validity > 0.6f)
            return BindState.Inherited;

        // Associated: has commitment but not fully locked
        if (commitment != null && belief.Confidence > 0.5f)
            return BindState.Associated;

        // Separate: raw observation, not bound
        return BindState.Separate;
    }

    private BeliefState ModulateBelief(BeliefState belief, InteroceptiveState feeling)
    {
        // PerceptionTrust control knob: scales confidence requirements
        float trustModifier = feeling.PerceptionTrust;

        return belief with
        {
            // Scale confidence by perception trust
            Confidence = belief.Confidence * trustModifier,

            // Be more conservative about threats when trust is low
            ThreatLevel = belief.ThreatLevel * (0.6f + trustModifier * 0.4f),

            // Hits are reliable if audio confirmed (cross-modal validation)
            HitConfidence = belief.HitConfirmed
                ? Math.Max(belief.HitConfidence * trustModifier, belief.AudioContribution * 0.8f)
                : belief.HitConfidence * trustModifier
        };
    }

    private ActionPlan ModulateActionPlan(ActionPlan plan, InteroceptiveState feeling)
    {
        // ActionSpeedModifier control knob: scales timing
        float speedMod = feeling.ActionSpeedModifier;
        var actions = plan.Actions.ToList();
        var reason = plan.Reason;
        var confidence = plan.Confidence;

        // Anxiety: add defensive movements
        if (feeling.Anxiety > 0.6f && plan.Mode == StrategicMode.Fight)
        {
            if (!actions.Any(a => a.Type is ActionType.MoveLeft or ActionType.MoveRight))
            {
                actions.Add(GameAction.Press(
                    Random.Shared.Next(2) == 0 ? ActionType.MoveLeft : ActionType.MoveRight,
                    (int)(100 * speedMod)));
                reason += " [anxious]";
            }
        }

        // Frustration: reduce confidence (may trigger strategy change)
        if (feeling.Frustration > 0.7f)
        {
            confidence *= 0.8f;
            reason += " [frustrated]";
        }

        // Focus: increase confidence
        if (feeling.Focus > 0.7f)
        {
            confidence = Math.Min(confidence * 1.2f, 1f);
            reason += " [focused]";
        }

        // Hysteresis info
        if (_committedState?.IsLocked == true)
        {
            reason += $" [locked:{_committedState.FramesSinceCommit}]";
        }
        else if (_committedState?.HasCandidate == true)
        {
            reason += $" [candidate:{_committedState.CandidateFrames}]";
        }

        return plan with
        {
            Actions = actions,
            Reason = reason,
            Confidence = confidence
        };
    }

    /// <summary>
    /// Reset state (e.g., on death/respawn).
    /// </summary>
    public void Reset()
    {
        _beliefLock.Reset();
        _rawBelief = null;
        _committedBelief = null;
        _currentFeeling = null;
        _lastPlan = null;
        _currentBindState = BindState.Separate;
    }

    /// <summary>
    /// Get diagnostic information.
    /// </summary>
    public string GetDiagnostics()
    {
        var beliefInfo = _committedBelief != null
            ? $"Belief: threat={_committedBelief.ThreatLevel:F2}, conf={_committedBelief.Confidence:F2}"
            : "Belief: none";

        var lockInfo = _committedState != null
            ? $"Lock: mode={_committedState.Mode}, locked={_committedState.IsLocked}, frames={_committedState.FramesSinceCommit}"
            : "Lock: none";

        var feelingInfo = _currentFeeling?.GetSummary() ?? "Feeling: none";
        var zInfo = _zScores.GetDiagnostics();
        var cueInfo = _cueRegistry.GetSummary();
        var refInfo = _refBuilder.GetDiagnostics();
        var centerInfo = _integrationBridge.GetDiagnostics();

        return $"""
            === COGNITIVE STATE ===
            {beliefInfo}
            {lockInfo}
            BindState: {_currentBindState}
            GateContext: {_currentGateContext}
            GapPressure: {ExpectationGapPressure:F2}
            {feelingInfo}
            {zInfo}
            {cueInfo}
            {refInfo}
            {centerInfo}
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
    private readonly WindowedStatistics _threatCountStats = new(100);
    private readonly WindowedStatistics _audioLevelStats = new(100);
    private readonly WindowedStatistics _audioEventStats = new(100);
    private readonly WindowedStatistics _healthChangeStats = new(100);
    private readonly WindowedStatistics _agreementStats = new(100);
    private readonly WindowedStatistics _volatilityStats = new(100);
    private readonly WindowedStatistics _controlStats = new(100);

    private int _lastHealth = 100;
    private int _lastAmmo = 30;
    private StrategicMode _lastMode;
    private float _modeChangesRecent;
    private int _shotsFired;
    private int _hitsRecent;

    public ZScoreInputs ComputeInputs(GameState state, AudioSnapshot audio, ActionPlan? lastPlan)
    {
        // Update sensory statistics
        _motionStats.Add(state.Detections.ThreatCount > 0 ? 1 : 0);
        _threatCountStats.Add(state.Detections.ThreatCount);
        _audioLevelStats.Add(audio.AverageLevel);
        _audioEventStats.Add(audio.Events.Count);

        int healthChange = state.Hud.Health - _lastHealth;
        _healthChangeStats.Add(healthChange);
        _lastHealth = state.Hud.Health;

        int ammoChange = state.Hud.AmmoClip - _lastAmmo;
        _lastAmmo = state.Hud.AmmoClip;

        // Belief volatility tracking
        if (lastPlan != null && lastPlan.Mode != _lastMode)
        {
            _modeChangesRecent++;
            _lastMode = lastPlan.Mode;
        }
        _modeChangesRecent *= 0.95f; // Exponential decay

        // Control efficacy tracking
        if (lastPlan != null && lastPlan.Actions.Any(a => a.Type == ActionType.Attack && a.IsPress))
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

        return new ZScoreInputs
        {
            VisionStats = new VisionStatistics
            {
                MotionZScore = (float)_motionStats.ZScore(state.Detections.ThreatCount > 0 ? 1 : 0),
                BrightnessZScore = 0,
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
                TargetSwitchesPerSecond = 0,
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
