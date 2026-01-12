namespace OKET.Agent.Learning.Knowledge;

/// <summary>
/// Valence represents the emotional/motivational direction of behavior.
/// - Positive: Approach, engage, attack, gain
/// - Negative: Avoid, retreat, defend, preserve
/// - Neutral: Observe, recalibrate, balance, reset
/// </summary>
public enum Valence
{
    /// <summary>
    /// Neutral valence: observation, recalibration, balance.
    /// Used when neither positive nor negative is clearly appropriate.
    /// Acts as a "reset" state for recalibration.
    /// </summary>
    Neutral = 0,

    /// <summary>
    /// Positive valence: approach, engage, attack, gain.
    /// Associated with confidence, opportunity, aggression.
    /// </summary>
    Positive = 1,

    /// <summary>
    /// Negative valence: avoid, retreat, defend, preserve.
    /// Associated with caution, threat, survival.
    /// </summary>
    Negative = -1
}

/// <summary>
/// Valence state with magnitude and confidence.
/// </summary>
public sealed class ValenceState
{
    /// <summary>Current valence direction.</summary>
    public Valence Direction { get; set; } = Valence.Neutral;

    /// <summary>Magnitude of valence [0, 1]. Higher = stronger pull.</summary>
    public float Magnitude { get; set; } = 0f;

    /// <summary>Confidence in current valence [0, 1].</summary>
    public float Confidence { get; set; } = 0.5f;

    /// <summary>How long in current valence (frames).</summary>
    public int Duration { get; set; } = 0;

    /// <summary>Accumulated positive signal.</summary>
    public float PositiveAccumulator { get; set; } = 0f;

    /// <summary>Accumulated negative signal.</summary>
    public float NegativeAccumulator { get; set; } = 0f;

    /// <summary>Whether currently in recalibration mode.</summary>
    public bool IsRecalibrating => Direction == Valence.Neutral && Magnitude < 0.2f;

    /// <summary>Net valence signal (-1 to +1).</summary>
    public float NetSignal => (PositiveAccumulator - NegativeAccumulator) /
                              Math.Max(1f, PositiveAccumulator + NegativeAccumulator + 0.001f);

    /// <summary>Whether valence is stable enough to act on.</summary>
    public bool IsStable => Duration > 5 && Confidence > 0.6f;

    /// <summary>Whether valence should transition.</summary>
    public bool ShouldTransition => !IsStable || Math.Abs(NetSignal) > 0.3f &&
                                    Math.Sign(NetSignal) != (int)Direction;

    public override string ToString() =>
        $"Valence: {Direction} (mag={Magnitude:F2}, conf={Confidence:F2}, dur={Duration})";
}

/// <summary>
/// Authorization result for valence transitions.
/// </summary>
public sealed class ValenceAuthorization
{
    public required Valence RequestedValence { get; init; }
    public required Valence AuthorizedValence { get; init; }
    public required bool IsAuthorized { get; init; }
    public required string Reason { get; init; }
    public required float Confidence { get; init; }
    public bool RequiresRecalibration { get; init; }
}

/// <summary>
/// Metabolized experience - processed and integrated into valence system.
/// </summary>
public sealed class MetabolizedExperience
{
    public required float[] State { get; init; }
    public required int Action { get; init; }
    public required float Reward { get; init; }
    public required Valence AssignedValence { get; init; }
    public required float ValenceContribution { get; init; }
    public required DateTime Timestamp { get; init; }
    public string? LessonLearned { get; init; }
}

/// <summary>
/// Valence Authorizer - controls transitions between positive, negative, and neutral modes.
/// Implements authorization logic to prevent harmful mode switches.
/// </summary>
public sealed class ValenceAuthorizer
{
    private readonly ValenceState _currentState = new();
    private readonly Queue<ValenceAuthorization> _authorizationHistory = new();
    private const int MaxHistorySize = 100;

    // Thresholds
    private readonly float _positiveThreshold;
    private readonly float _negativeThreshold;
    private readonly float _neutralThreshold;
    private readonly int _minDurationBeforeSwitch;

    // Statistics
    private int _totalAuthorizations;
    private int _deniedTransitions;
    private int _forcedRecalibrations;

    public ValenceState CurrentState => _currentState;
    public int TotalAuthorizations => _totalAuthorizations;
    public int DeniedTransitions => _deniedTransitions;

    public ValenceAuthorizer(
        float positiveThreshold = 0.4f,
        float negativeThreshold = -0.4f,
        float neutralThreshold = 0.15f,
        int minDurationBeforeSwitch = 10)
    {
        _positiveThreshold = positiveThreshold;
        _negativeThreshold = negativeThreshold;
        _neutralThreshold = neutralThreshold;
        _minDurationBeforeSwitch = minDurationBeforeSwitch;
    }

    /// <summary>
    /// Request authorization to transition to a new valence.
    /// </summary>
    public ValenceAuthorization RequestTransition(Valence requested, float signal, float urgency = 0f)
    {
        _totalAuthorizations++;

        // Check if transition is allowed
        var (authorized, reason) = EvaluateTransition(requested, signal, urgency);

        var authorization = new ValenceAuthorization
        {
            RequestedValence = requested,
            AuthorizedValence = authorized ? requested : _currentState.Direction,
            IsAuthorized = authorized,
            Reason = reason,
            Confidence = CalculateTransitionConfidence(requested, signal),
            RequiresRecalibration = ShouldRecalibrate(requested, signal)
        };

        // Record history
        _authorizationHistory.Enqueue(authorization);
        while (_authorizationHistory.Count > MaxHistorySize)
            _authorizationHistory.Dequeue();

        // Apply if authorized
        if (authorized)
        {
            ApplyTransition(requested, signal);
        }
        else
        {
            _deniedTransitions++;
        }

        // Check for forced recalibration
        if (authorization.RequiresRecalibration && _currentState.Direction != Valence.Neutral)
        {
            ForceRecalibration("Signal uncertainty too high");
        }

        return authorization;
    }

    /// <summary>
    /// Evaluate whether a transition should be authorized.
    /// </summary>
    private (bool authorized, string reason) EvaluateTransition(Valence requested, float signal, float urgency)
    {
        // Always allow transition to neutral (recalibration)
        if (requested == Valence.Neutral)
        {
            return (true, "Neutral transition always allowed for recalibration");
        }

        // Check minimum duration
        if (_currentState.Duration < _minDurationBeforeSwitch && urgency < 0.8f)
        {
            return (false, $"Minimum duration not met ({_currentState.Duration}/{_minDurationBeforeSwitch})");
        }

        // Check signal strength
        if (requested == Valence.Positive && signal < _positiveThreshold)
        {
            return (false, $"Positive signal too weak ({signal:F2} < {_positiveThreshold:F2})");
        }

        if (requested == Valence.Negative && signal > _negativeThreshold)
        {
            return (false, $"Negative signal too weak ({signal:F2} > {_negativeThreshold:F2})");
        }

        // Check for conflicting signals
        if (Math.Abs(_currentState.NetSignal - signal) > 0.5f && urgency < 0.7f)
        {
            return (false, "Conflicting signals - consider recalibration");
        }

        // High urgency overrides other checks
        if (urgency > 0.8f)
        {
            return (true, $"Urgency override ({urgency:F2})");
        }

        // Check confidence threshold
        if (_currentState.Confidence > 0.7f && _currentState.Direction != requested)
        {
            // Need strong evidence to override high-confidence state
            if (Math.Abs(signal) < 0.6f)
            {
                return (false, "Current state has high confidence - need stronger signal");
            }
        }

        return (true, "Transition authorized");
    }

    /// <summary>
    /// Check if recalibration is needed.
    /// </summary>
    private bool ShouldRecalibrate(Valence requested, float signal)
    {
        // Recalibrate if signals are mixed
        if (Math.Abs(signal) < _neutralThreshold)
            return true;

        // Recalibrate if confidence is low
        if (_currentState.Confidence < 0.3f)
            return true;

        // Recalibrate if frequent transitions
        var recentTransitions = _authorizationHistory
            .TakeLast(10)
            .Count(a => a.IsAuthorized && a.AuthorizedValence != _currentState.Direction);

        if (recentTransitions > 5)
            return true;

        return false;
    }

    /// <summary>
    /// Calculate confidence in transition.
    /// </summary>
    private static float CalculateTransitionConfidence(Valence requested, float signal)
    {
        float signalStrength = Math.Abs(signal);
        float directionMatch = requested switch
        {
            Valence.Positive => signal > 0 ? 1f : 0f,
            Valence.Negative => signal < 0 ? 1f : 0f,
            Valence.Neutral => 1f - signalStrength,
            _ => 0.5f
        };

        return signalStrength * directionMatch;
    }

    /// <summary>
    /// Apply a transition.
    /// </summary>
    private void ApplyTransition(Valence newValence, float signal)
    {
        if (newValence != _currentState.Direction)
        {
            _currentState.Duration = 0;
        }

        _currentState.Direction = newValence;
        _currentState.Magnitude = Math.Abs(signal);
        _currentState.Confidence = CalculateTransitionConfidence(newValence, signal);
        _currentState.Duration++;
    }

    /// <summary>
    /// Force a recalibration to neutral.
    /// </summary>
    public void ForceRecalibration(string reason)
    {
        _currentState.Direction = Valence.Neutral;
        _currentState.Magnitude = 0f;
        _currentState.Confidence = 0.5f;
        _currentState.Duration = 0;
        _currentState.PositiveAccumulator *= 0.5f; // Decay accumulators
        _currentState.NegativeAccumulator *= 0.5f;
        _forcedRecalibrations++;
    }

    /// <summary>
    /// Update accumulators with new signal.
    /// </summary>
    public void AccumulateSignal(float signal)
    {
        const float decay = 0.95f;

        // Decay existing accumulators
        _currentState.PositiveAccumulator *= decay;
        _currentState.NegativeAccumulator *= decay;

        // Add new signal
        if (signal > 0)
            _currentState.PositiveAccumulator += signal;
        else
            _currentState.NegativeAccumulator += Math.Abs(signal);

        _currentState.Duration++;
    }

    public string GetDiagnostics() => $"""
        ValenceAuthorizer:
          State: {_currentState}
          Net Signal: {_currentState.NetSignal:F2}
          Authorizations: {_totalAuthorizations}
          Denied: {_deniedTransitions}
          Recalibrations: {_forcedRecalibrations}
        """;
}

/// <summary>
/// Valence Metabolizer - processes experiences and integrates them into the valence system.
/// "Metabolizes" raw experience into meaningful valence signals.
/// </summary>
public sealed class ValenceMetabolizer
{
    private readonly Queue<MetabolizedExperience> _metabolizedHistory = new();
    private readonly Dictionary<Valence, float> _valenceRewardSums = new();
    private readonly Dictionary<Valence, int> _valenceCounts = new();
    private const int MaxHistorySize = 1000;

    // Metabolization weights
    private readonly float _rewardWeight;
    private readonly float _healthWeight;
    private readonly float _threatWeight;
    private readonly float _progressWeight;

    // Statistics
    private int _totalMetabolized;
    private float _positiveRewardSum;
    private float _negativeRewardSum;

    public int TotalMetabolized => _totalMetabolized;

    public ValenceMetabolizer(
        float rewardWeight = 0.4f,
        float healthWeight = 0.3f,
        float threatWeight = 0.2f,
        float progressWeight = 0.1f)
    {
        _rewardWeight = rewardWeight;
        _healthWeight = healthWeight;
        _threatWeight = threatWeight;
        _progressWeight = progressWeight;

        foreach (Valence v in Enum.GetValues<Valence>())
        {
            _valenceRewardSums[v] = 0f;
            _valenceCounts[v] = 0;
        }
    }

    /// <summary>
    /// Metabolize a raw experience into valence signals.
    /// </summary>
    public MetabolizedExperience Metabolize(
        float[] state,
        int action,
        float reward,
        float[] nextState,
        bool terminal)
    {
        _totalMetabolized++;

        // Calculate valence contribution from multiple signals
        float healthSignal = CalculateHealthSignal(state, nextState);
        float threatSignal = CalculateThreatSignal(state, nextState);
        float progressSignal = CalculateProgressSignal(state, nextState, reward);

        // Combine signals with weights
        float valenceContribution =
            reward * _rewardWeight +
            healthSignal * _healthWeight +
            threatSignal * _threatWeight +
            progressSignal * _progressWeight;

        // Determine assigned valence
        Valence assignedValence = DetermineValence(valenceContribution, state, action);

        // Generate lesson learned
        string? lesson = GenerateLesson(state, action, reward, assignedValence);

        var metabolized = new MetabolizedExperience
        {
            State = state,
            Action = action,
            Reward = reward,
            AssignedValence = assignedValence,
            ValenceContribution = valenceContribution,
            Timestamp = DateTime.UtcNow,
            LessonLearned = lesson
        };

        // Update statistics
        _valenceRewardSums[assignedValence] += reward;
        _valenceCounts[assignedValence]++;

        if (reward > 0) _positiveRewardSum += reward;
        else _negativeRewardSum += Math.Abs(reward);

        // Store in history
        _metabolizedHistory.Enqueue(metabolized);
        while (_metabolizedHistory.Count > MaxHistorySize)
            _metabolizedHistory.Dequeue();

        return metabolized;
    }

    /// <summary>
    /// Calculate health-based signal.
    /// </summary>
    private static float CalculateHealthSignal(float[] state, float[] nextState)
    {
        float currentHealth = state.Length > FeatureIndices.Health ? state[FeatureIndices.Health] : 1f;
        float nextHealth = nextState.Length > FeatureIndices.Health ? nextState[FeatureIndices.Health] : 1f;

        float healthDelta = nextHealth - currentHealth;

        // Positive signal if health improved or stable at high level
        // Negative signal if health dropped
        if (healthDelta > 0) return healthDelta * 2f; // Amplify healing
        if (healthDelta < 0) return healthDelta * 3f; // Amplify damage (negative)
        if (currentHealth > 0.7f) return 0.1f; // Small positive for being healthy
        if (currentHealth < 0.3f) return -0.2f; // Negative for low health

        return 0f;
    }

    /// <summary>
    /// Calculate threat-based signal.
    /// </summary>
    private static float CalculateThreatSignal(float[] state, float[] nextState)
    {
        float currentThreats = state.Length > FeatureIndices.ThreatsInFov ? state[FeatureIndices.ThreatsInFov] : 0f;
        float nextThreats = nextState.Length > FeatureIndices.ThreatsInFov ? nextState[FeatureIndices.ThreatsInFov] : 0f;
        float dangerLevel = state.Length > FeatureIndices.DangerLevel ? state[FeatureIndices.DangerLevel] : 0f;

        float threatDelta = nextThreats - currentThreats;

        // Positive signal if threats reduced
        // Negative signal if threats increased
        if (threatDelta < 0) return Math.Abs(threatDelta) * 0.3f; // Killed/escaped zombies
        if (threatDelta > 0) return -threatDelta * 0.2f; // More zombies appeared
        if (dangerLevel > 0.7f) return -0.3f; // High danger is negative
        if (currentThreats == 0) return 0.2f; // No threats is positive

        return 0f;
    }

    /// <summary>
    /// Calculate progress-based signal.
    /// </summary>
    private static float CalculateProgressSignal(float[] state, float[] nextState, float reward)
    {
        float wave = state.Length > FeatureIndices.Wave ? state[FeatureIndices.Wave] : 0f;
        float nextWave = nextState.Length > FeatureIndices.Wave ? nextState[FeatureIndices.Wave] : 0f;

        // Wave progression is very positive
        if (nextWave > wave) return 0.5f;

        // Survival over time is slightly positive
        if (reward >= 0) return 0.05f;

        return 0f;
    }

    /// <summary>
    /// Determine which valence this experience belongs to.
    /// </summary>
    private static Valence DetermineValence(float contribution, float[] state, int action)
    {
        // Strong positive contribution → Positive valence
        if (contribution > 0.2f)
            return Valence.Positive;

        // Strong negative contribution → Negative valence
        if (contribution < -0.2f)
            return Valence.Negative;

        // Weak signal → Neutral valence (needs recalibration)
        return Valence.Neutral;
    }

    /// <summary>
    /// Generate a lesson from this experience.
    /// </summary>
    private static string? GenerateLesson(float[] state, int action, float reward, Valence valence)
    {
        string actionName = GetActionName(action);

        if (reward > 0.1f && valence == Valence.Positive)
            return $"{actionName} produced positive outcome in this context";

        if (reward < -0.1f && valence == Valence.Negative)
            return $"{actionName} led to negative outcome - consider alternatives";

        if (valence == Valence.Neutral)
            return $"Uncertain outcome for {actionName} - more data needed";

        return null;
    }

    /// <summary>
    /// Get average reward for each valence.
    /// </summary>
    public Dictionary<Valence, float> GetValenceAverages()
    {
        var averages = new Dictionary<Valence, float>();
        foreach (Valence v in Enum.GetValues<Valence>())
        {
            averages[v] = _valenceCounts[v] > 0
                ? _valenceRewardSums[v] / _valenceCounts[v]
                : 0f;
        }
        return averages;
    }

    /// <summary>
    /// Get recommended valence based on current metabolized data.
    /// </summary>
    public Valence GetRecommendedValence(float[] currentState)
    {
        var averages = GetValenceAverages();

        // If positive experiences dominate, recommend positive
        if (averages[Valence.Positive] > averages[Valence.Negative] * 1.5f)
            return Valence.Positive;

        // If negative experiences dominate (survival mode)
        if (averages[Valence.Negative] > averages[Valence.Positive] * 1.5f)
            return Valence.Negative;

        // Otherwise, recommend neutral for recalibration
        return Valence.Neutral;
    }

    private static string GetActionName(int action) => action switch
    {
        0 => "Idle",
        1 => "Fight",
        2 => "Kite",
        3 => "Reload",
        4 => "Heal",
        5 => "Repair",
        6 => "Reposition",
        7 => "Buy",
        8 => "Support",
        9 => "Unstick",
        _ => $"Action_{action}"
    };

    public string GetDiagnostics()
    {
        var averages = GetValenceAverages();
        return $"""
            ValenceMetabolizer:
              Total Metabolized: {_totalMetabolized:N0}
              Positive Reward Sum: {_positiveRewardSum:F2}
              Negative Reward Sum: {_negativeRewardSum:F2}
              Valence Averages:
                Positive: {averages[Valence.Positive]:F3} ({_valenceCounts[Valence.Positive]} samples)
                Negative: {averages[Valence.Negative]:F3} ({_valenceCounts[Valence.Negative]} samples)
                Neutral: {averages[Valence.Neutral]:F3} ({_valenceCounts[Valence.Neutral]} samples)
            """;
    }
}

/// <summary>
/// Maps actions to their natural valence.
/// </summary>
public static class ActionValenceMapping
{
    public static Valence GetNaturalValence(int action) => action switch
    {
        0 => Valence.Neutral,   // Idle - neutral
        1 => Valence.Positive,  // Fight - approach/engage
        2 => Valence.Negative,  // Kite - retreat/avoid
        3 => Valence.Neutral,   // Reload - preparation
        4 => Valence.Negative,  // Heal - self-preservation
        5 => Valence.Neutral,   // Repair - maintenance
        6 => Valence.Neutral,   // Reposition - adjustment
        7 => Valence.Neutral,   // Buy - preparation
        8 => Valence.Positive,  // Support - approach
        9 => Valence.Neutral,   // Unstick - recovery
        _ => Valence.Neutral
    };

    public static int[] GetActionsForValence(Valence valence) => valence switch
    {
        Valence.Positive => new[] { 1, 8 },           // Fight, Support
        Valence.Negative => new[] { 2, 4 },           // Kite, Heal
        Valence.Neutral => new[] { 0, 3, 5, 6, 7, 9 }, // Idle, Reload, Repair, Reposition, Buy, Unstick
        _ => new[] { 0 }
    };
}
