using OKET.Core.State;
using OKET.Core.Actions;
using OKET.Core.Cognition;

namespace OKET.Agent.Cognition;

/// <summary>
/// Hysteresis gate that prevents belief/mode thrashing.
/// Implements: "I will not switch unless the new state wins by a margin for a minimum duration."
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

    // Configurable thresholds (these become Feeling control knobs)
    public float ThreatMargin { get; set; } = 0.15f;      // New threat must exceed current by this margin
    public float ConfidenceMargin { get; set; } = 0.1f;   // New belief must be this much more confident
    public int MinCommitFrames { get; set; } = 10;        // ~330ms at 30fps
    public int MinLockDuration { get; set; } = 15;        // Can't switch for this many frames after committing
    public float UrgencyOverride { get; set; } = 0.8f;    // Skip hysteresis if urgency exceeds this

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

        // Apply feeling modulation to thresholds
        float effectiveMargin = ThreatMargin * feeling.CommitmentConfidence;
        int effectiveMinFrames = (int)(MinCommitFrames / Math.Max(feeling.ActionSpeedModifier, 0.5f));

        // Check for urgency override (high urgency = skip hysteresis)
        bool urgentOverride = feeling.Urgency > UrgencyOverride;

        // First commitment (cold start)
        if (_committedBelief == null)
        {
            Commit(proposedBelief, proposedMode, proposedTargetId);
            return GetCommittedState();
        }

        // Check if locked
        if (IsLocked && !urgentOverride)
        {
            // Still locked - ignore proposal, return committed
            return GetCommittedState();
        }

        // Evaluate the proposal
        var evaluation = EvaluateProposal(proposedBelief, proposedMode, proposedTargetId, effectiveMargin);

        if (evaluation == ProposalResult.Accept)
        {
            // Clear win - commit immediately
            Commit(proposedBelief, proposedMode, proposedTargetId);
            return GetCommittedState();
        }
        else if (evaluation == ProposalResult.Candidate)
        {
            // Potential switch - track as candidate
            if (IsSameCandidate(proposedBelief, proposedMode, proposedTargetId))
            {
                _candidateFrames++;

                // Candidate has been consistent long enough?
                if (_candidateFrames >= effectiveMinFrames)
                {
                    Commit(proposedBelief, proposedMode, proposedTargetId);
                    return GetCommittedState();
                }
            }
            else
            {
                // New candidate - reset counter
                _candidateBelief = proposedBelief;
                _candidateMode = proposedMode;
                _candidateTargetId = proposedTargetId;
                _candidateStartTime = DateTime.UtcNow;
                _candidateFrames = 1;
            }
        }
        else
        {
            // Proposal rejected - clear any candidate
            _candidateBelief = null;
            _candidateFrames = 0;
        }

        // Return committed state
        return GetCommittedState();
    }

    private ProposalResult EvaluateProposal(
        BeliefState proposed,
        StrategicMode proposedMode,
        int? proposedTargetId,
        float margin)
    {
        // Mode change evaluation
        bool modeChanged = proposedMode != _committedMode;

        // Target change evaluation
        bool targetChanged = proposedTargetId != _committedTargetId && proposedTargetId != null;

        // Threat level change evaluation
        float threatDelta = proposed.ThreatLevel - (_committedBelief?.ThreatLevel ?? 0);

        // Confidence evaluation
        float confidenceDelta = proposed.Confidence - (_committedBelief?.Confidence ?? 0);

        // Special cases: always accept certain transitions
        if (_committedMode == StrategicMode.Idle && proposedMode != StrategicMode.Idle)
        {
            // Leaving idle - accept any action
            return ProposalResult.Accept;
        }

        if (proposed.ThreatLevel > 0.7f && _committedBelief?.ThreatLevel < 0.3f)
        {
            // Major threat escalation - accept immediately
            return ProposalResult.Accept;
        }

        if (proposed.HealthRisk > 0.8f && proposedMode == StrategicMode.Kite)
        {
            // Critical health + kite mode - accept immediately
            return ProposalResult.Accept;
        }

        // Standard evaluation: does proposal win by margin?
        if (modeChanged || targetChanged)
        {
            // Need to win by margin
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
            // Same mode and target - just update belief
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

    private void Commit(BeliefState belief, StrategicMode mode, int? targetId)
    {
        _committedBelief = belief;
        _committedMode = mode;
        _committedTargetId = targetId;
        _commitTime = DateTime.UtcNow;
        FramesSinceCommit = 0;

        // Clear candidate
        _candidateBelief = null;
        _candidateFrames = 0;
    }

    private CommittedState GetCommittedState()
    {
        return new CommittedState
        {
            Belief = _committedBelief!,
            Mode = _committedMode,
            TargetId = _committedTargetId,
            FramesSinceCommit = FramesSinceCommit,
            IsLocked = IsLocked,
            HasCandidate = _candidateBelief != null,
            CandidateFrames = _candidateFrames
        };
    }

    /// <summary>
    /// Force a mode (for safety overrides).
    /// </summary>
    public void ForceMode(StrategicMode mode)
    {
        _committedMode = mode;
        FramesSinceCommit = 0;
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
    }

    private enum ProposalResult
    {
        Accept,     // Commit immediately
        Candidate,  // Track as candidate, commit after duration
        Reject      // Ignore, keep current
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
}
