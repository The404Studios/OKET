namespace OKET.Core.Memory;

/// <summary>
/// Types of expectation gaps (what should be present but isn't).
///
/// The meta-cognitive closure: checking what we're thinking
/// against the space of what we haven't thought but could invalidate posture.
///
/// Absence is information. These gaps are mechanical, not philosophical.
/// </summary>
public enum GapType
{
    /// <summary>Audio expected but not present (e.g., hit sound after confirmed crosshair alignment).</summary>
    MissingAudioConfirmation,

    /// <summary>Visual confirmation expected but not present.</summary>
    MissingVisualConfirmation,

    /// <summary>Modalities disagree (audio says X, vision says Y).</summary>
    ModalityConflict,

    /// <summary>Silence where signal is normally present.</summary>
    UnexpectedSilence,

    /// <summary>Action produced no feedback.</summary>
    ActionWithoutFeedback,

    /// <summary>Committed posture but strain rising.</summary>
    PostureUnderStrain,

    /// <summary>High confidence but Z₁ disagreement.</summary>
    ConfidenceWithoutAgreement,

    /// <summary>Cue fired but outcome didn't match prediction.</summary>
    CueMismatch,

    /// <summary>Inherited belief but recent contradiction.</summary>
    InheritanceContradicted,

    /// <summary>Expected target but lost track.</summary>
    LostTarget,

    /// <summary>Control actions happening but no observed effect.</summary>
    ControlWithoutEffect
}

/// <summary>
/// An expectation gap - something that should be present but isn't.
///
/// This is the mechanical implementation of:
/// "A posture is only valid if it survives not just the evidence that supports it,
/// but the acknowledged absence of evidence that could refute it."
///
/// Gaps show up as elevated Z-scores, hesitation triggers, forced unlocks.
/// They're not hypotheticals - they're missing expected signals.
/// </summary>
public sealed class ExpectationGap
{
    public GapType Type { get; }
    public string Description { get; }
    public DateTime DetectedAt { get; }
    public float Severity { get; private set; }
    public int DurationFrames { get; private set; }
    public bool IsResolved { get; private set; }

    /// <summary>
    /// What was expected.
    /// </summary>
    public string Expected { get; }

    /// <summary>
    /// What was actually observed (or "nothing").
    /// </summary>
    public string Observed { get; }

    /// <summary>
    /// Reference that this gap applies to (if any).
    /// </summary>
    public RefId? RelatedRef { get; set; }

    public ExpectationGap(GapType type, string expected, string observed, float severity = 0.5f)
    {
        Type = type;
        Expected = expected;
        Observed = observed;
        Severity = Math.Clamp(severity, 0f, 1f);
        DetectedAt = DateTime.UtcNow;
        Description = $"{type}: expected '{expected}', got '{observed}'";
    }

    /// <summary>
    /// Update the gap state (call each frame while gap persists).
    /// </summary>
    public void Update(bool stillPresent)
    {
        if (stillPresent)
        {
            DurationFrames++;
            // Severity increases the longer the gap persists
            Severity = Math.Min(1f, Severity + 0.01f);
        }
        else
        {
            IsResolved = true;
        }
    }

    /// <summary>
    /// Age in milliseconds.
    /// </summary>
    public double AgeMs => (DateTime.UtcNow - DetectedAt).TotalMilliseconds;

    public override string ToString() =>
        $"Gap[{Type}] Sev={Severity:F2} Dur={DurationFrames} Resolved={IsResolved}";
}

/// <summary>
/// Tracks active expectation gaps and computes the "unknown pressure" signal.
///
/// This is the meta-check: what the system is acknowledging it doesn't know.
///
/// The system should:
/// - Slow down when gaps are present
/// - Yield when confidence is high but gaps are severe
/// - Demote inheritance when gaps persist
/// - Never confuse "nothing wrong yet" with "confirmed safe"
/// </summary>
public sealed class ExpectationGapTracker
{
    private readonly List<ExpectationGap> _activeGaps = new();
    private readonly List<ExpectationGap> _recentResolved = new();
    private readonly object _lock = new();

    private const int MaxRecentResolved = 50;

    /// <summary>
    /// Total pressure from all active gaps [0, 1].
    /// High = system acknowledges significant unknowns.
    /// </summary>
    public float TotalGapPressure
    {
        get
        {
            lock (_lock)
            {
                if (_activeGaps.Count == 0) return 0f;
                // Combine severities with diminishing returns
                float sum = 0f;
                foreach (var gap in _activeGaps)
                    sum += gap.Severity * (1f - sum * 0.3f);
                return Math.Min(1f, sum);
            }
        }
    }

    /// <summary>
    /// Number of active gaps.
    /// </summary>
    public int ActiveGapCount
    {
        get { lock (_lock) return _activeGaps.Count; }
    }

    /// <summary>
    /// Whether any critical gaps are present.
    /// </summary>
    public bool HasCriticalGaps
    {
        get
        {
            lock (_lock)
            {
                return _activeGaps.Any(g => g.Severity > 0.7f && g.DurationFrames > 10);
            }
        }
    }

    /// <summary>
    /// Record a new expectation gap.
    /// </summary>
    public ExpectationGap RecordGap(GapType type, string expected, string observed, float severity = 0.5f)
    {
        lock (_lock)
        {
            // Check if similar gap already exists
            var existing = _activeGaps.FirstOrDefault(g =>
                g.Type == type && !g.IsResolved);

            if (existing != null)
            {
                existing.Update(true);
                return existing;
            }

            var gap = new ExpectationGap(type, expected, observed, severity);
            _activeGaps.Add(gap);
            return gap;
        }
    }

    /// <summary>
    /// Resolve a gap (the expected signal was received).
    /// </summary>
    public void ResolveGap(GapType type)
    {
        lock (_lock)
        {
            var gap = _activeGaps.FirstOrDefault(g => g.Type == type && !g.IsResolved);
            if (gap != null)
            {
                gap.Update(false);
                _activeGaps.Remove(gap);
                _recentResolved.Add(gap);

                // Prune old resolved
                while (_recentResolved.Count > MaxRecentResolved)
                    _recentResolved.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Update all gaps and prune resolved ones.
    /// </summary>
    public void UpdateAll()
    {
        lock (_lock)
        {
            var toRemove = new List<ExpectationGap>();
            foreach (var gap in _activeGaps)
            {
                if (gap.IsResolved)
                {
                    toRemove.Add(gap);
                    _recentResolved.Add(gap);
                }
            }

            foreach (var gap in toRemove)
                _activeGaps.Remove(gap);

            while (_recentResolved.Count > MaxRecentResolved)
                _recentResolved.RemoveAt(0);
        }
    }

    /// <summary>
    /// Check for common expectation gaps based on cognitive state.
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
        // Missing audio when visual confirms action
        if (hasVisualConfirmation && !hasAudioConfirmation)
            RecordGap(GapType.MissingAudioConfirmation, "hit sound", "silence", 0.4f);
        else
            ResolveGap(GapType.MissingAudioConfirmation);

        // Missing visual when audio suggests event
        if (hasAudioConfirmation && !hasVisualConfirmation)
            RecordGap(GapType.MissingVisualConfirmation, "visual effect", "nothing", 0.4f);
        else
            ResolveGap(GapType.MissingVisualConfirmation);

        // Modality conflict
        if (modalityAgreement < -0.5f)
            RecordGap(GapType.ModalityConflict, "agreement", $"Z₁={modalityAgreement:F2}", 0.6f);
        else
            ResolveGap(GapType.ModalityConflict);

        // Action without feedback
        if (!actionHadFeedback)
            RecordGap(GapType.ActionWithoutFeedback, "action feedback", "nothing", 0.3f);
        else
            ResolveGap(GapType.ActionWithoutFeedback);

        // Posture under strain
        if (strainTrend > 0.3f)
            RecordGap(GapType.PostureUnderStrain, "stable strain", $"rising {strainTrend:F2}", 0.5f);
        else
            ResolveGap(GapType.PostureUnderStrain);

        // High confidence but low agreement
        if (confidenceLevel > 0.7f && modalityAgreement < 0.2f)
            RecordGap(GapType.ConfidenceWithoutAgreement, "corroboration", "unconfirmed", 0.5f);
        else
            ResolveGap(GapType.ConfidenceWithoutAgreement);

        // Lost target
        if (!hasTarget)
            RecordGap(GapType.LostTarget, "target", "lost", 0.4f);
        else
            ResolveGap(GapType.LostTarget);

        // Control without effect
        if (!controlHadEffect)
            RecordGap(GapType.ControlWithoutEffect, "effect", "nothing", 0.4f);
        else
            ResolveGap(GapType.ControlWithoutEffect);
    }

    /// <summary>
    /// Get all active gaps.
    /// </summary>
    public IReadOnlyList<ExpectationGap> GetActiveGaps()
    {
        lock (_lock) return _activeGaps.ToList();
    }

    /// <summary>
    /// Get summary.
    /// </summary>
    public string GetSummary()
    {
        lock (_lock)
        {
            if (_activeGaps.Count == 0)
                return "Gaps: none active";

            var gapList = string.Join(", ", _activeGaps.Select(g => $"{g.Type}({g.Severity:F2})"));
            return $"Gaps: {_activeGaps.Count} active, pressure={TotalGapPressure:F2}\n  [{gapList}]";
        }
    }
}
