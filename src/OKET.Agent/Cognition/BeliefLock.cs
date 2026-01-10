using OKET.Core.State;
using OKET.Core.Actions;
using OKET.Core.Cognition;

namespace OKET.Agent.Cognition;

/// <summary>
/// Hysteresis gate that prevents belief/mode thrashing.
/// Implements: "I will not switch unless the new state wins by a margin for a minimum duration."
///
/// CRITICAL: Also implements forced unlock when strain rises while outcomes worsen.
/// Without this, hysteresis can trap the agent in wrong beliefs.
///
/// This is the difference between "looks like a bot" and "looks deliberate."
/// </summary>
public sealed class BeliefLock
{
    // Current committed state
    private BeliefState? _committedBelief;
    private StrategicMode _committedMode = StrategicMode.Idle;
    private int? _committedTargetId;
    private DateTime _commitTime = DateTime.MinValue;

    // Candidate state (proposed but not yet committed)
    private BeliefState? _candidateBelief;
    private StrategicMode _candidateMode;
    private int? _candidateTargetId;
    private DateTime _candidateStartTime;
    private int _candidateFrames;

    // Strain/outcome tracking for forced unlock
    private readonly Queue<float> _strainHistory = new();
    private readonly Queue<float> _outcomeHistory = new();
    private const int HistorySize = 30; // ~1 second at 30fps
    private int _framesSinceLastUnlock;

    // Configurable thresholds
    public float ThreatMargin { get; set; } = 0.15f;
    public float ConfidenceMargin { get; set; } = 0.1f;
    public int MinCommitFrames { get; set; } = 10;
    public int MinLockDuration { get; set; } = 15;
    public float UrgencyOverride { get; set; } = 0.8f;

    // Forced unlock thresholds
    public float StrainRiseThreshold { get; set; } = 0.3f;    // Strain must rise by this much
    public float OutcomeDeclineThreshold { get; set; } = 0.2f; // Outcomes must decline by this much
    public int MinFramesBetweenUnlocks { get; set; } = 45;     // ~1.5s cooldown between forced unlocks

    /// <summary>Current committed belief.</summary>
    public BeliefState? CommittedBelief => _committedBelief;

    /// <summary>Current committed mode.</summary>
    public StrategicMode CommittedMode => _committedMode;

    /// <summary>Current committed target.</summary>
    public int? CommittedTargetId => _committedTargetId;

    /// <summary>Frames since last commit.</summary>
    public int FramesSinceCommit { get; private set; }

    /// <summary>Whether currently locked (can't switch).</summary>
    public bool IsLocked => FramesSinceCommit < MinLockDuration;

    /// <summary>Whether forced unlock was triggered this frame.</summary>
    public bool ForcedUnlockTriggered { get; private set; }

    /// <summary>Current strain trend (for diagnostics).</summary>
    public float StrainTrend { get; private set; }

    /// <summary>Current outcome trend (for diagnostics).</summary>
    public float OutcomeTrend { get; private set; }

    /// <summary>
    /// Process a new belief/mode proposal. Returns the committed state to use.
    /// </summary>
    public CommittedState Process(
        BeliefState proposedBelief,
        StrategicMode proposedMode,
        int? proposedTargetId,
        InteroceptiveState feeling)
    {
        FramesSinceCommit++;
        _framesSinceLastUnlock++;
        ForcedUnlockTriggered = false;

        // Track strain and outcome history
        UpdateHistory(feeling.SystemStrain, feeling.OutcomeTrend);

        // Apply feeling modulation to thresholds
        float effectiveMargin = ThreatMargin * feeling.CommitmentConfidence;
        int effectiveMinFrames = (int)(MinCommitFrames / Math.Max(feeling.ActionSpeedModifier, 0.5f));

        // Check for urgency override
        bool urgentOverride = feeling.Urgency > UrgencyOverride;

        // CHECK FOR FORCED UNLOCK: strain rising + outcomes worsening OR validity compromised
        bool forcedUnlock = CheckForcedUnlock() || CheckValidityUnlock(feeling);
        if (forcedUnlock)
        {
            ForcedUnlockTriggered = true;
            _framesSinceLastUnlock = 0;
            // Don't commit yet - just unlock and let normal evaluation proceed
        }

        // First commitment (cold start)
        if (_committedBelief == null)
        {
            Commit(proposedBelief, proposedMode, proposedTargetId, "cold_start");
            return GetCommittedState(feeling);
        }

        // Check if locked (unless forced unlock or urgent)
        if (IsLocked && !urgentOverride && !forcedUnlock)
        {
            return GetCommittedState(feeling);
        }

        // Evaluate the proposal
        var evaluation = EvaluateProposal(proposedBelief, proposedMode, proposedTargetId, effectiveMargin);

        // Forced unlock lowers the bar for acceptance
        if (forcedUnlock && evaluation == ProposalResult.Reject)
        {
            // Under forced unlock, even marginal improvements are accepted as candidates
            evaluation = ProposalResult.Candidate;
        }

        if (evaluation == ProposalResult.Accept)
        {
            Commit(proposedBelief, proposedMode, proposedTargetId, "clear_win");
            return GetCommittedState(feeling);
        }
        else if (evaluation == ProposalResult.Candidate)
        {
            if (IsSameCandidate(proposedBelief, proposedMode, proposedTargetId))
            {
                _candidateFrames++;

                // Under forced unlock, commit faster
                int framesToCommit = forcedUnlock ? effectiveMinFrames / 2 : effectiveMinFrames;

                if (_candidateFrames >= framesToCommit)
                {
                    Commit(proposedBelief, proposedMode, proposedTargetId,
                        forcedUnlock ? "forced_unlock" : "candidate_won");
                    return GetCommittedState(feeling);
                }
            }
            else
            {
                _candidateBelief = proposedBelief;
                _candidateMode = proposedMode;
                _candidateTargetId = proposedTargetId;
                _candidateStartTime = DateTime.UtcNow;
                _candidateFrames = 1;
            }
        }
        else
        {
            _candidateBelief = null;
            _candidateFrames = 0;
        }

        return GetCommittedState(feeling);
    }

    private void UpdateHistory(float strain, float outcome)
    {
        _strainHistory.Enqueue(strain);
        _outcomeHistory.Enqueue(outcome);

        while (_strainHistory.Count > HistorySize)
            _strainHistory.Dequeue();
        while (_outcomeHistory.Count > HistorySize)
            _outcomeHistory.Dequeue();

        // Calculate trends
        if (_strainHistory.Count >= 10)
        {
            var strainList = _strainHistory.ToList();
            var outcomeList = _outcomeHistory.ToList();

            int mid = strainList.Count / 2;

            float recentStrain = strainList.Skip(mid).Average();
            float olderStrain = strainList.Take(mid).Average();
            StrainTrend = recentStrain - olderStrain;

            float recentOutcome = outcomeList.Skip(mid).Average();
            float olderOutcome = outcomeList.Take(mid).Average();
            OutcomeTrend = recentOutcome - olderOutcome;
        }
    }

    /// <summary>
    /// Check if we should force unlock due to rising strain + declining outcomes.
    /// This prevents the agent from being trapped in wrong beliefs.
    /// </summary>
    private bool CheckForcedUnlock()
    {
        // Cooldown between forced unlocks
        if (_framesSinceLastUnlock < MinFramesBetweenUnlocks)
            return false;

        // Need enough history
        if (_strainHistory.Count < 10)
            return false;

        // Check: strain rising AND outcomes declining
        bool strainRising = StrainTrend > StrainRiseThreshold;
        bool outcomesWorsening = OutcomeTrend < -OutcomeDeclineThreshold;

        return strainRising && outcomesWorsening;
    }

    /// <summary>
    /// Check if we should force unlock due to compromised validity.
    /// Validity is the explicit signal that posture can't carry current load.
    /// </summary>
    private bool CheckValidityUnlock(InteroceptiveState feeling)
    {
        // Cooldown between forced unlocks
        if (_framesSinceLastUnlock < MinFramesBetweenUnlocks)
            return false;

        // Only trigger on validity signal if we've been locked for a while
        // (to give posture a chance to prove itself)
        if (FramesSinceCommit < MinLockDuration * 2)
            return false;

        return feeling.ValidityCompromised;
    }

    private ProposalResult EvaluateProposal(
        BeliefState proposed,
        StrategicMode proposedMode,
        int? proposedTargetId,
        float margin)
    {
        bool modeChanged = proposedMode != _committedMode;
        bool targetChanged = proposedTargetId != _committedTargetId && proposedTargetId != null;

        float threatDelta = proposed.ThreatLevel - (_committedBelief?.ThreatLevel ?? 0);
        float confidenceDelta = proposed.Confidence - (_committedBelief?.Confidence ?? 0);

        // Special cases: always accept certain transitions
        if (_committedMode == StrategicMode.Idle && proposedMode != StrategicMode.Idle)
        {
            return ProposalResult.Accept;
        }

        if (proposed.ThreatLevel > 0.7f && _committedBelief?.ThreatLevel < 0.3f)
        {
            return ProposalResult.Accept;
        }

        if (proposed.HealthRisk > 0.8f && proposedMode == StrategicMode.Kite)
        {
            return ProposalResult.Accept;
        }

        // Standard evaluation
        if (modeChanged || targetChanged)
        {
            bool betterThreatAssessment = threatDelta > margin || Math.Abs(threatDelta) < 0.1f;
            bool betterConfidence = confidenceDelta > -ConfidenceMargin;

            if (betterThreatAssessment && betterConfidence)
            {
                return ProposalResult.Candidate;
            }
            else
            {
                return ProposalResult.Reject;
            }
        }
        else
        {
            return ProposalResult.Accept;
        }
    }

    private bool IsSameCandidate(BeliefState belief, StrategicMode mode, int? targetId)
    {
        if (_candidateBelief == null) return false;

        return mode == _candidateMode &&
               targetId == _candidateTargetId &&
               Math.Abs(belief.ThreatLevel - _candidateBelief.ThreatLevel) < 0.2f;
    }

    private void Commit(BeliefState belief, StrategicMode mode, int? targetId, string reason)
    {
        _committedBelief = belief;
        _committedMode = mode;
        _committedTargetId = targetId;
        _commitTime = DateTime.UtcNow;
        FramesSinceCommit = 0;
        LastCommitReason = reason;

        _candidateBelief = null;
        _candidateFrames = 0;
    }

    /// <summary>Reason for last commit (for diagnostics).</summary>
    public string LastCommitReason { get; private set; } = "";

    private CommittedState GetCommittedState(InteroceptiveState feeling)
    {
        return new CommittedState
        {
            Belief = _committedBelief!,
            Mode = _committedMode,
            TargetId = _committedTargetId,
            FramesSinceCommit = FramesSinceCommit,
            IsLocked = IsLocked,
            HasCandidate = _candidateBelief != null,
            CandidateFrames = _candidateFrames,
            ForcedUnlock = ForcedUnlockTriggered,
            StrainTrend = StrainTrend,
            OutcomeTrend = OutcomeTrend,
            Validity = feeling.Validity,
            ValidityCompromised = feeling.ValidityCompromised,
            CommitReason = LastCommitReason
        };
    }

    /// <summary>
    /// Force a mode (for safety overrides).
    /// </summary>
    public void ForceMode(StrategicMode mode)
    {
        _committedMode = mode;
        FramesSinceCommit = 0;
        LastCommitReason = "safety_override";
    }

    /// <summary>
    /// Reset all state (e.g., on death/respawn).
    /// </summary>
    public void Reset()
    {
        _committedBelief = null;
        _committedMode = StrategicMode.Idle;
        _committedTargetId = null;
        _candidateBelief = null;
        _candidateFrames = 0;
        FramesSinceCommit = 0;
        _strainHistory.Clear();
        _outcomeHistory.Clear();
        _framesSinceLastUnlock = 0;
        LastCommitReason = "";
    }

    private enum ProposalResult
    {
        Accept,
        Candidate,
        Reject
    }
}

/// <summary>
/// Output from the BeliefLock gate.
/// </summary>
public sealed record CommittedState
{
    public required BeliefState Belief { get; init; }
    public required StrategicMode Mode { get; init; }
    public int? TargetId { get; init; }
    public int FramesSinceCommit { get; init; }
    public bool IsLocked { get; init; }
    public bool HasCandidate { get; init; }
    public int CandidateFrames { get; init; }
    public bool ForcedUnlock { get; init; }
    public float StrainTrend { get; init; }
    public float OutcomeTrend { get; init; }
    public float Validity { get; init; }
    public bool ValidityCompromised { get; init; }
    public string CommitReason { get; init; } = "";
}
