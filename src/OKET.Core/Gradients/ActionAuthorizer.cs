namespace OKET.Core.Gradients;

/// <summary>
/// Action Authorization: Act on mapped similarity + expected outcomes.
///
/// PRINCIPLE: Even if something is "never seen before," you don't freeze.
/// You act based on:
/// 1. Nearest prototypes
/// 2. Historical outcomes for similar situations
/// 3. Risk-weighted expected value
///
/// Score(a) = E[reward | prototypes] - Risk(a) + InfoGain(a)
///
/// This enables correct behavior BEFORE naming.
/// </summary>
public sealed class ActionAuthorizer
{
    private readonly PrototypeLibrary _prototypes;
    private readonly TransitionMemory _memory;

    // Mode switching based on novelty/confidence
    private LearningMode _currentMode = LearningMode.Balanced;
    private float _explorationRate = 0.15f;
    private float _riskTolerance = 0.5f;

    // Action history for pattern detection
    private readonly Queue<AuthorizedAction> _actionHistory = new();
    private const int MaxActionHistory = 50;

    // Statistics
    private int _totalAuthorizations;
    private int _exploratoryActions;
    private int _exploitativeActions;

    public LearningMode CurrentMode => _currentMode;
    public float ExplorationRate => _explorationRate;
    public int TotalAuthorizations => _totalAuthorizations;

    public ActionAuthorizer(PrototypeLibrary prototypes, TransitionMemory memory)
    {
        _prototypes = prototypes;
        _memory = memory;
    }

    /// <summary>
    /// Authorize an action based on current situation.
    /// </summary>
    public AuthorizationResult Authorize(Superstate situation, float urgency = 0.5f)
    {
        _totalAuthorizations++;

        // Get situation summary
        var summary = situation.GetSummary();

        // Determine mode based on novelty and confidence
        UpdateMode(situation);

        // Get expected outcomes for each action
        var candidates = new List<ActionCandidate>();

        foreach (ActionType action in Enum.GetValues<ActionType>())
        {
            var expected = _memory.GetExpectedOutcome(situation.Signature, action);
            var score = ComputeActionScore(action, expected, summary, urgency);

            candidates.Add(new ActionCandidate
            {
                Action = action,
                Expected = expected,
                Score = score.score,
                Risk = score.risk,
                InfoGain = score.infoGain,
                Confidence = expected.Confidence
            });
        }

        // Sort by score
        candidates = candidates.OrderByDescending(c => c.Score).ToList();

        // Select action based on mode
        ActionCandidate selected;
        bool isExploratory = false;

        if (_currentMode == LearningMode.Learning &&
            Random.Shared.NextDouble() < _explorationRate)
        {
            // Exploration: pick action with high info gain
            selected = candidates.OrderByDescending(c => c.InfoGain).First();
            isExploratory = true;
            _exploratoryActions++;
        }
        else
        {
            // Exploitation: pick highest score
            selected = candidates.First();
            _exploitativeActions++;
        }

        // Record in history
        var authorized = new AuthorizedAction
        {
            Action = selected.Action,
            Score = selected.Score,
            Confidence = selected.Confidence,
            IsExploratory = isExploratory,
            SituationType = summary.Type,
            Timestamp = DateTime.UtcNow
        };
        _actionHistory.Enqueue(authorized);
        while (_actionHistory.Count > MaxActionHistory)
            _actionHistory.Dequeue();

        return new AuthorizationResult
        {
            AuthorizedAction = selected.Action,
            Score = selected.Score,
            Risk = selected.Risk,
            Confidence = selected.Confidence,
            IsExploratory = isExploratory,
            Alternatives = candidates.Skip(1).Take(3).Select(c => c.Action).ToArray(),
            Reasoning = GenerateReasoning(selected, summary, isExploratory)
        };
    }

    /// <summary>
    /// Compute score for an action.
    /// </summary>
    private (float score, float risk, float infoGain) ComputeActionScore(
        ActionType action,
        ExpectedOutcome expected,
        SituationSummary situation,
        float urgency)
    {
        // Base score from expected success
        float baseScore = expected.Success;

        // Risk penalty (scaled by risk tolerance)
        float riskPenalty = expected.Risk * (1f - _riskTolerance);

        // Info gain bonus (higher when confidence is low)
        float infoGainBonus = expected.Confidence < 0.5f
            ? expected.InfoGain * 0.3f * _explorationRate
            : 0;

        // Urgency modifier (favor fast actions when urgent)
        float urgencyMod = 0;
        if (urgency > 0.7f)
        {
            if (action is ActionType.Engage or ActionType.Retreat or ActionType.Kite)
                urgencyMod = 0.2f;
            if (action is ActionType.Wait or ActionType.Observe)
                urgencyMod = -0.2f;
        }

        // Situation-specific modifiers
        float situationMod = GetSituationModifier(action, situation);

        float score = baseScore - riskPenalty + infoGainBonus + urgencyMod + situationMod;

        return (score, expected.Risk, expected.InfoGain);
    }

    private static float GetSituationModifier(ActionType action, SituationSummary situation)
    {
        return situation.Type switch
        {
            SuperstateType.ThreatApproaching => action switch
            {
                ActionType.Engage => 0.2f,
                ActionType.Kite => 0.3f,
                ActionType.Retreat => 0.1f,
                ActionType.Wait => -0.3f,
                _ => 0
            },
            SuperstateType.MultiThreat => action switch
            {
                ActionType.Kite => 0.3f,
                ActionType.Retreat => 0.2f,
                ActionType.Engage => -0.1f,
                _ => 0
            },
            SuperstateType.OpportunityCluster => action switch
            {
                ActionType.Approach => 0.2f,
                ActionType.Interact => 0.3f,
                ActionType.Observe => 0.1f,
                _ => 0
            },
            SuperstateType.Clear => action switch
            {
                ActionType.Explore => 0.2f,
                ActionType.Observe => 0.1f,
                ActionType.Engage => -0.2f,
                _ => 0
            },
            _ => 0
        };
    }

    /// <summary>
    /// Update learning mode based on current situation novelty.
    /// </summary>
    private void UpdateMode(Superstate situation)
    {
        // Count novel nodes
        int novelCount = situation.Nodes.Count(n => n.IsNovel);
        float noveltyRatio = situation.Nodes.Count > 0
            ? (float)novelCount / situation.Nodes.Count
            : 0;

        // Update mode
        if (noveltyRatio > 0.5f || situation.Confidence < 0.3f)
        {
            _currentMode = LearningMode.Learning;
            _explorationRate = Math.Min(0.3f, _explorationRate + 0.02f);
            _riskTolerance = Math.Max(0.3f, _riskTolerance - 0.02f);
        }
        else if (noveltyRatio < 0.1f && situation.Confidence > 0.7f)
        {
            _currentMode = LearningMode.Training;
            _explorationRate = Math.Max(0.05f, _explorationRate - 0.01f);
            _riskTolerance = Math.Min(0.7f, _riskTolerance + 0.01f);
        }
        else
        {
            _currentMode = LearningMode.Balanced;
            _explorationRate = 0.15f;
            _riskTolerance = 0.5f;
        }
    }

    /// <summary>
    /// Generate human-readable reasoning for the decision.
    /// </summary>
    private static string GenerateReasoning(
        ActionCandidate selected,
        SituationSummary situation,
        bool isExploratory)
    {
        string modeReason = isExploratory ? "exploring unknown" : "exploiting known";
        string situationDesc = situation.Type.ToString();
        string confidenceDesc = selected.Confidence switch
        {
            < 0.3f => "low confidence",
            < 0.7f => "moderate confidence",
            _ => "high confidence"
        };

        return $"{selected.Action}: score={selected.Score:F2} ({modeReason}, {situationDesc}, {confidenceDesc})";
    }

    /// <summary>
    /// Record feedback for a previous authorization.
    /// </summary>
    public void RecordFeedback(ActionType action, TransitionOutcome outcome)
    {
        // Update exploration rate based on outcome
        if (outcome.Success > 0.5f)
        {
            // Success - can reduce exploration
            _explorationRate = Math.Max(0.05f, _explorationRate - 0.005f);
        }
        else if (outcome.Success < -0.5f)
        {
            // Failure - might need more exploration
            _explorationRate = Math.Min(0.3f, _explorationRate + 0.01f);
        }
    }

    /// <summary>
    /// Get diagnostics.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"""
            === ACTION AUTHORIZER ===
            Mode: {_currentMode}
            Exploration Rate: {_explorationRate:F2}
            Risk Tolerance: {_riskTolerance:F2}
            Total: {_totalAuthorizations} (explore={_exploratoryActions}, exploit={_exploitativeActions})

            Recent Actions:
            {string.Join("\n", _actionHistory.TakeLast(5).Select(a => $"  {a.Action}: {a.Score:F2} ({(a.IsExploratory ? "explore" : "exploit")})"))}
            =========================
            """;
    }
}

/// <summary>
/// Learning mode determines exploration vs exploitation balance.
/// </summary>
public enum LearningMode
{
    /// <summary>High novelty - explore more, slower actions, more memory writes.</summary>
    Learning,

    /// <summary>High confidence - exploit best policy, fast actions, fewer probes.</summary>
    Training,

    /// <summary>Balanced exploration and exploitation.</summary>
    Balanced
}

/// <summary>
/// Result of action authorization.
/// </summary>
public readonly struct AuthorizationResult
{
    /// <summary>The authorized action to take.</summary>
    public ActionType AuthorizedAction { get; init; }

    /// <summary>Score of the action.</summary>
    public float Score { get; init; }

    /// <summary>Risk level.</summary>
    public float Risk { get; init; }

    /// <summary>Confidence in this decision.</summary>
    public float Confidence { get; init; }

    /// <summary>Was this an exploratory action?</summary>
    public bool IsExploratory { get; init; }

    /// <summary>Alternative actions considered.</summary>
    public ActionType[] Alternatives { get; init; }

    /// <summary>Human-readable reasoning.</summary>
    public string Reasoning { get; init; }
}

/// <summary>
/// Candidate action being evaluated.
/// </summary>
internal readonly struct ActionCandidate
{
    public ActionType Action { get; init; }
    public ExpectedOutcome Expected { get; init; }
    public float Score { get; init; }
    public float Risk { get; init; }
    public float InfoGain { get; init; }
    public float Confidence { get; init; }
}

/// <summary>
/// Record of an authorized action.
/// </summary>
internal readonly struct AuthorizedAction
{
    public ActionType Action { get; init; }
    public float Score { get; init; }
    public float Confidence { get; init; }
    public bool IsExploratory { get; init; }
    public SuperstateType SituationType { get; init; }
    public DateTime Timestamp { get; init; }
}
