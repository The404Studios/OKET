using OKET.Core.Memory;
using OKET.Core.Operators;
using OKET.Core.Cognition;
using OKET.Core.State;
using OKET.Core.Audio;
using OKET.Core.Actions;
using OKET.Agent.Cognition;

namespace OKET.Agent.Memory;

/// <summary>
/// Builds references from cognitive events.
///
/// This is how "thinking gets ears and eyes":
/// - Vision/audio events become references
/// - References get tags
/// - Tags become boundaries through survival
/// - Chains form: Detection → Track → Commitment → Action → Outcome
///
/// The builder sits between perception and memory, creating
/// the substrate for operational understanding.
/// </summary>
public sealed class ReferenceBuilder
{
    private readonly ReferenceGraph _graph = new();
    private readonly TagIndex _tagIndex = new();
    private readonly ExpectationGapTracker _gapTracker = new();

    // Track recent references for linking
    private RefId? _lastDetectionRef;
    private RefId? _lastAudioRef;
    private RefId? _lastBeliefRef;
    private RefId? _lastCommitmentRef;
    private RefId? _lastActionRef;

    public ReferenceGraph Graph => _graph;
    public TagIndex Tags => _tagIndex;
    public ExpectationGapTracker Gaps => _gapTracker;

    /// <summary>
    /// Build references after perception stage.
    /// </summary>
    public void AfterPerception(GameState state, AudioSnapshot audio, ZScoreStack zScores)
    {
        // Create detection references
        if (state.Detections.ThreatCount > 0)
        {
            var detectionRef = new ReferenceNode(RefType.Detection);
            detectionRef.SetMetric("threat_count", state.Detections.ThreatCount);
            detectionRef.SetMetric("z0_vision", zScores.Z0.Vision_Motion);

            // Tag based on state
            if (state.Detections.PrimaryThreat != null)
            {
                detectionRef.AddTag("HasPrimaryTarget");
                detectionRef.SetMetric("primary_distance", state.Detections.PrimaryThreat.EstimatedDistance ?? 0f);
            }
            if (state.Detections.ThreatCount > 2)
                detectionRef.AddTag("MultipleThreat");

            _graph.Add(detectionRef);
            _tagIndex.Index(detectionRef);
            _lastDetectionRef = detectionRef.Id;
        }

        // Create audio event references
        if (audio.Events.Count > 0)
        {
            var audioRef = new ReferenceNode(RefType.AudioEvent);
            audioRef.SetMetric("event_count", audio.Events.Count);
            audioRef.SetMetric("level", audio.AverageLevel);
            audioRef.SetMetric("z0_audio", zScores.Z0.Audio_Level);

            if (audio.HasThreatSounds)
                audioRef.AddTag("ThreatSound");
            if (audio.HasDamageSounds)
                audioRef.AddTag("DamageSound");
            if (audio.HasCombatSounds)
                audioRef.AddTag("CombatSound");

            // Link to detection if present
            if (_lastDetectionRef.HasValue)
                audioRef.LinkTo(_lastDetectionRef.Value);

            _graph.Add(audioRef);
            _tagIndex.Index(audioRef);
            _lastAudioRef = audioRef.Id;
        }

        // Create Z-spike references for significant deviations
        if (Math.Abs(zScores.Z1_PerceptualAgreement) > 1.5f)
        {
            var spikeRef = new ReferenceNode(RefType.ZSpike);
            spikeRef.SetMetric("z1", zScores.Z1_PerceptualAgreement);
            spikeRef.AddTag(zScores.Z1_PerceptualAgreement > 0 ? "AVAgree" : "AVDisagree");

            if (_lastDetectionRef.HasValue)
                spikeRef.LinkTo(_lastDetectionRef.Value);
            if (_lastAudioRef.HasValue)
                spikeRef.LinkTo(_lastAudioRef.Value);

            _graph.Add(spikeRef);
            _tagIndex.Index(spikeRef);
        }
    }

    /// <summary>
    /// Build references after fusion and Z-stack.
    /// </summary>
    public void AfterFusion(
        BeliefState belief,
        InteroceptiveState feeling,
        ZScoreStack zScores)
    {
        // Create belief candidate reference
        var beliefRef = new ReferenceNode(RefType.BeliefCandidate, BindState.Associated);
        beliefRef.SetMetric("confidence", belief.Confidence);
        beliefRef.SetMetric("threat_level", belief.ThreatLevel);
        beliefRef.SetMetric("validity", feeling.Validity);
        beliefRef.SetMetric("z4_strain", zScores.SystemStrain);

        // Tag based on feeling state
        if (feeling.ShouldHesitate)
            beliefRef.AddTag("Hesitating");
        if (feeling.MustActNow)
            beliefRef.AddTag("Urgent");
        if (feeling.ValidityCompromised)
            beliefRef.AddTag("Compromised");
        if (feeling.Validity > 0.6f)
            beliefRef.AddTag("ValidPosture");

        // Tag based on emotional state
        if (feeling.Anxiety > 0.6f)
            beliefRef.AddTag("Anxious");
        if (feeling.Focus > 0.6f)
            beliefRef.AddTag("Focused");

        // Link to perception refs
        if (_lastDetectionRef.HasValue)
            beliefRef.LinkTo(_lastDetectionRef.Value);
        if (_lastAudioRef.HasValue)
            beliefRef.LinkTo(_lastAudioRef.Value);

        _graph.Add(beliefRef);
        _tagIndex.Index(beliefRef);
        _lastBeliefRef = beliefRef.Id;

        // Create strain trend reference if significant
        if (Math.Abs(zScores.SystemStrain) > 1.0f)
        {
            var strainRef = new ReferenceNode(RefType.StrainTrend);
            strainRef.SetMetric("z4", zScores.SystemStrain);
            strainRef.AddTag(zScores.SystemStrain > 1.5f ? "HighStrain" : "ModerateStrain");

            _graph.Add(strainRef);
            _tagIndex.Index(strainRef);
        }

        // Create agreement reference
        if (zScores.Z1_PerceptualAgreement > 0.5f)
        {
            var agreeRef = new ReferenceNode(RefType.Agreement);
            agreeRef.SetMetric("z1", zScores.Z1_PerceptualAgreement);
            agreeRef.AddTag("MultimodalAgree");

            if (_lastDetectionRef.HasValue)
                agreeRef.LinkTo(_lastDetectionRef.Value);
            if (_lastAudioRef.HasValue)
                agreeRef.LinkTo(_lastAudioRef.Value);

            _graph.Add(agreeRef);
            _tagIndex.Index(agreeRef);
        }
        else if (zScores.Z1_PerceptualAgreement < -0.5f)
        {
            var contradictRef = new ReferenceNode(RefType.Contradiction);
            contradictRef.SetMetric("z1", zScores.Z1_PerceptualAgreement);
            contradictRef.AddTag("ModalityConflict");

            _graph.Add(contradictRef);
            _tagIndex.Index(contradictRef);
        }
    }

    /// <summary>
    /// Build references after BeliefLock and action execution.
    /// </summary>
    public void AfterCommitment(
        CommittedState commitment,
        ActionPlan plan,
        InteroceptiveState feeling)
    {
        // Create commitment reference (this is key - it passed the sink)
        var commitRef = new ReferenceNode(RefType.Commitment, BindState.Associated);
        commitRef.SetMetric("validity", commitment.Validity);
        commitRef.SetMetric("frames_locked", commitment.FramesSinceCommit);

        commitRef.AddTag($"Mode_{commitment.Mode}");
        if (commitment.IsLocked)
            commitRef.AddTag("Locked");
        if (commitment.ForcedUnlock)
            commitRef.AddTag("ForcedUnlock");

        // Link to belief
        if (_lastBeliefRef.HasValue)
        {
            commitRef.LinkTo(_lastBeliefRef.Value);
            commitRef.Parent = _lastBeliefRef;
        }

        _graph.Add(commitRef);
        _tagIndex.Index(commitRef);
        _lastCommitmentRef = commitRef.Id;

        // Create action plan reference
        var actionRef = new ReferenceNode(RefType.ActionPlan);
        actionRef.SetMetric("confidence", plan.Confidence);
        actionRef.SetMetric("action_count", plan.Actions.Count);

        actionRef.AddTag($"Plan_{plan.Mode}");

        // Link to commitment
        actionRef.LinkTo(commitRef.Id);
        actionRef.Parent = commitRef.Id;

        _graph.Add(actionRef);
        _tagIndex.Index(actionRef);
        _lastActionRef = actionRef.Id;
    }

    /// <summary>
    /// Build references after observing action outcomes.
    /// </summary>
    public void AfterOutcome(
        float strainDelta,
        float outcomeDelta,
        bool hitConfirmed,
        bool survived)
    {
        // Create outcome reference
        var outcomeRef = new ReferenceNode(RefType.ActionOutcome);
        outcomeRef.SetMetric("strain_delta", strainDelta);
        outcomeRef.SetMetric("outcome_delta", outcomeDelta);

        if (hitConfirmed)
            outcomeRef.AddTag("HitConfirmed");
        if (survived)
            outcomeRef.AddTag("Survived");
        else
            outcomeRef.AddTag("Failed");

        if (outcomeDelta > 0.1f)
            outcomeRef.AddTag("Improving");
        else if (outcomeDelta < -0.1f)
            outcomeRef.AddTag("Declining");

        // Link to action
        if (_lastActionRef.HasValue)
        {
            outcomeRef.LinkTo(_lastActionRef.Value);
            outcomeRef.Parent = _lastActionRef;
        }

        _graph.Add(outcomeRef);
        _tagIndex.Index(outcomeRef);

        // Update validities in graph
        _graph.UpdateValidities(strainDelta, outcomeDelta);

        // Record outcome for tags
        if (_lastActionRef.HasValue)
            _tagIndex.RecordOutcome(_lastActionRef.Value, survived);
        if (_lastCommitmentRef.HasValue)
            _tagIndex.RecordOutcome(_lastCommitmentRef.Value, survived);
    }

    /// <summary>
    /// Check expectation gaps based on current state.
    /// </summary>
    public void CheckExpectations(
        bool hasAudioConfirmation,
        bool hasVisualConfirmation,
        float modalityAgreement,
        bool actionHadFeedback,
        float strainTrend,
        float confidenceLevel,
        bool hasTarget,
        bool controlHadEffect)
    {
        _gapTracker.CheckExpectations(
            hasAudioConfirmation,
            hasVisualConfirmation,
            modalityAgreement,
            actionHadFeedback,
            strainTrend,
            confidenceLevel,
            hasTarget,
            controlHadEffect);

        // Create gap references for significant gaps
        foreach (var gap in _gapTracker.GetActiveGaps().Where(g => g.Severity > 0.5f))
        {
            // Check if we already have a recent ref for this gap
            var existingGapRefs = _graph.Query(
                type: RefType.ExpectationGap,
                tag: gap.Type.ToString(),
                maxAge: TimeSpan.FromSeconds(1));

            if (existingGapRefs.Count == 0)
            {
                var gapRef = new ReferenceNode(RefType.ExpectationGap);
                gapRef.SetMetric("severity", gap.Severity);
                gapRef.SetMetric("duration", gap.DurationFrames);
                gapRef.AddTag(gap.Type.ToString());
                gapRef.AddTag("Gap");

                _graph.Add(gapRef);
                _tagIndex.Index(gapRef);
            }
        }

        _gapTracker.UpdateAll();
    }

    /// <summary>
    /// Update all memory structures (salience decay, gap decay, pruning).
    /// Call every frame.
    /// </summary>
    /// <param name="currentStrain">Current system strain (Z₄).</param>
    /// <param name="outcomeTrend">Current outcome trend (for gap recovery).</param>
    public void Update(float currentStrain = 0f, float outcomeTrend = 0f)
    {
        // Update graph with salience decay and strain-weighted pruning
        _graph.UpdateAll(currentStrain);

        // Update gaps with outcome-aware decay
        _gapTracker.UpdateAll(outcomeTrend);
    }

    /// <summary>
    /// Get comprehensive diagnostics.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"""
            === REFERENCE MEMORY ===
            {_graph.GetSummary()}
            {_tagIndex.GetSummary()}
            {_gapTracker.GetSummary()}
            ========================
            """;
    }

    /// <summary>
    /// Query the last N seconds of operational history.
    /// </summary>
    public IReadOnlyList<ReferenceNode> GetRecentHistory(TimeSpan window)
    {
        return _graph.GetRecent(window);
    }

    /// <summary>
    /// Get the causal chain from a commitment back to perception.
    /// </summary>
    public IReadOnlyList<ReferenceNode> GetCommitmentChain(RefId commitmentId)
    {
        return _graph.GetChain(commitmentId);
    }

    /// <summary>
    /// Find moments where specific tags co-occurred.
    /// </summary>
    public IReadOnlyList<RefId> FindCoOccurrence(params string[] tags)
    {
        return _tagIndex.GetByAllTags(tags).ToList();
    }
}
