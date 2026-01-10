using OKET.Core.State;
using OKET.Core.Actions;
using OKET.Core.Cognition;

namespace OKET.Agent.Cognition;

/// <summary>
/// Computes interoceptive (feeling) state from raw observations and history.
/// This is the "global stability layer" that answers:
/// "How stable is my understanding of reality right now?"
/// </summary>
public sealed class InteroceptiveProcessor
{
    // History for computing trends and stability
    private readonly Queue<HistoryEntry> _history = new();
    private const int HistorySize = 60; // ~2 seconds at 30fps

    // Running statistics for Z-score-like normalization
    private readonly ExponentialStatistics _predictionErrorStats = new(0.05);
    private readonly ExponentialStatistics _controlSuccessStats = new(0.05);
    private readonly ExponentialStatistics _threatLevelStats = new(0.05);

    // Recent predictions for error calculation
    private float _predictedThreatLevel;
    private float _predictedHealth;
    private bool _predictedHit;

    // Action outcome tracking
    private int _shotsFired;
    private int _hitsConfirmed;
    private DateTime _lastActionTime;
    private StrategicMode _lastMode;
    private int _modeChanges;

    public InteroceptiveState Process(
        GameState gameState,
        BeliefState belief,
        ActionPlan? lastPlan,
        ZScoreStack zScores)
    {
        // Record history
        RecordHistory(gameState, belief, lastPlan);

        // Calculate each component
        float predictionError = CalculatePredictionError(gameState, belief);
        float threatPressure = CalculateThreatPressure(gameState, belief);
        float controlConfidence = CalculateControlConfidence(gameState, belief, lastPlan);
        float sensoryAlignment = belief.SensoryAgreement;
        float outcomeTrend = CalculateOutcomeTrend();
        float beliefStability = CalculateBeliefStability();
        float actionCoherence = CalculateActionCoherence(lastPlan);

        // Update predictions for next frame
        UpdatePredictions(gameState, belief, lastPlan);

        return new InteroceptiveState
        {
            PredictionError = predictionError,
            ThreatPressure = threatPressure,
            ControlConfidence = controlConfidence,
            SensoryAlignment = sensoryAlignment,
            OutcomeTrend = outcomeTrend,
            BeliefStability = beliefStability,
            ActionCoherence = actionCoherence
        };
    }

    private void RecordHistory(GameState state, BeliefState belief, ActionPlan? plan)
    {
        _history.Enqueue(new HistoryEntry
        {
            Timestamp = DateTime.UtcNow,
            Health = state.Hud.Health,
            ThreatLevel = belief.ThreatLevel,
            BeliefConfidence = belief.Confidence,
            Mode = plan?.Mode ?? StrategicMode.Idle,
            WasHit = state.FramesSinceDamage == 0,
            DealtDamage = belief.HitConfirmed
        });

        while (_history.Count > HistorySize)
            _history.Dequeue();
    }

    private float CalculatePredictionError(GameState state, BeliefState belief)
    {
        // Compare predictions to reality
        float error = 0;

        // Threat level prediction error
        float threatError = Math.Abs(belief.ThreatLevel - _predictedThreatLevel);
        error += threatError * 0.3f;

        // Health prediction error (did we take unexpected damage?)
        float healthError = Math.Abs(state.Hud.Health - _predictedHealth) / 100f;
        error += healthError * 0.4f;

        // Hit prediction error (did our shot land as expected?)
        if (_predictedHit != belief.HitConfirmed)
        {
            error += 0.3f;
        }

        // Normalize with running statistics
        _predictionErrorStats.Add(error);
        float normalizedError = (float)Math.Clamp(error / Math.Max(_predictionErrorStats.Mean + _predictionErrorStats.StdDev * 2, 0.1), 0, 1);

        return normalizedError;
    }

    private float CalculateThreatPressure(GameState state, BeliefState belief)
    {
        // Current threat level
        float currentThreat = belief.ThreatLevel;

        // Threat trend (is it increasing?)
        float threatTrend = 0;
        if (_history.Count >= 10)
        {
            var recent = _history.TakeLast(10).ToList();
            var older = _history.Take(Math.Max(1, _history.Count - 10)).ToList();

            float recentAvg = recent.Average(h => h.ThreatLevel);
            float olderAvg = older.Average(h => h.ThreatLevel);
            threatTrend = recentAvg - olderAvg;
        }

        // Health pressure (low health = high pressure)
        float healthPressure = 1f - state.Hud.Health / 100f;

        // Combine
        float pressure = currentThreat * 0.4f +
                         Math.Max(0, threatTrend) * 0.3f +
                         healthPressure * 0.3f;

        _threatLevelStats.Add(pressure);

        return Math.Clamp(pressure, 0f, 1f);
    }

    private float CalculateControlConfidence(GameState state, BeliefState belief, ActionPlan? plan)
    {
        // Track shot effectiveness
        if (plan != null && plan.Actions.Any(a => a.Type == ActionType.Attack && a.IsPress))
        {
            _shotsFired++;
            _lastActionTime = DateTime.UtcNow;
        }

        if (belief.HitConfirmed)
        {
            _hitsConfirmed++;
        }

        // Calculate hit rate
        float hitRate = _shotsFired > 0 ? (float)_hitsConfirmed / _shotsFired : 0.5f;

        // Movement effectiveness (are we changing position?)
        float movementEffectiveness = state.IsStuck ? 0f : 1f;

        // Survival effectiveness (are we staying alive?)
        float survivalScore = state.Hud.IsDead ? 0f : 1f;

        // Combine
        float control = hitRate * 0.4f + movementEffectiveness * 0.3f + survivalScore * 0.3f;

        _controlSuccessStats.Add(control);

        // Decay counters slowly
        if (_shotsFired > 100)
        {
            _shotsFired = _shotsFired / 2;
            _hitsConfirmed = _hitsConfirmed / 2;
        }

        return Math.Clamp(control, 0f, 1f);
    }

    private float CalculateOutcomeTrend()
    {
        if (_history.Count < 20) return 0;

        var entries = _history.ToList();
        int midpoint = entries.Count / 2;

        float recentHealth = entries.Skip(midpoint).Average(h => h.Health);
        float olderHealth = entries.Take(midpoint).Average(h => h.Health);

        float healthTrend = (recentHealth - olderHealth) / 50f; // Normalize to roughly [-1, 1]

        // Also consider threat reduction
        float recentThreat = entries.Skip(midpoint).Average(h => h.ThreatLevel);
        float olderThreat = entries.Take(midpoint).Average(h => h.ThreatLevel);

        float threatTrend = (olderThreat - recentThreat); // Positive if threat decreasing

        return Math.Clamp(healthTrend * 0.6f + threatTrend * 0.4f, -1f, 1f);
    }

    private float CalculateBeliefStability()
    {
        if (_history.Count < 10) return 0.5f;

        var entries = _history.ToList();

        // Calculate variance in belief confidence
        float avgConfidence = entries.Average(h => h.BeliefConfidence);
        float variance = entries.Average(h => (h.BeliefConfidence - avgConfidence) * (h.BeliefConfidence - avgConfidence));

        // Calculate mode change rate
        int modeChanges = 0;
        for (int i = 1; i < entries.Count; i++)
        {
            if (entries[i].Mode != entries[i - 1].Mode)
                modeChanges++;
        }
        float changeRate = modeChanges / (float)entries.Count;

        // High variance or high change rate = low stability
        float instability = variance * 2f + changeRate * 0.5f;

        return Math.Clamp(1f - instability, 0f, 1f);
    }

    private float CalculateActionCoherence(ActionPlan? plan)
    {
        if (plan == null) return 0.5f;

        // Track mode changes
        if (plan.Mode != _lastMode)
        {
            _modeChanges++;
            _lastMode = plan.Mode;
        }

        // Decay mode changes over time
        if (_history.Count > 0 && _history.Count % 30 == 0)
        {
            _modeChanges = Math.Max(0, _modeChanges - 1);
        }

        // High confidence + low mode thrashing = coherent
        float coherence = plan.Confidence * 0.6f + (1f - Math.Min(_modeChanges / 10f, 1f)) * 0.4f;

        return Math.Clamp(coherence, 0f, 1f);
    }

    private void UpdatePredictions(GameState state, BeliefState belief, ActionPlan? plan)
    {
        // Predict next frame's threat level (simple: assume similar)
        _predictedThreatLevel = belief.ThreatLevel;

        // Predict health (assume no damage unless under attack)
        _predictedHealth = state.Hud.Health;
        if (belief.IsUnderAttack)
        {
            _predictedHealth = Math.Max(0, state.Hud.Health - 5); // Expect some damage
        }

        // Predict hit (if firing at target)
        _predictedHit = false;
        if (plan != null && plan.Mode == StrategicMode.Fight && state.Aim.IsOnTarget)
        {
            _predictedHit = true;
        }
    }

    private sealed record HistoryEntry
    {
        public DateTime Timestamp { get; init; }
        public int Health { get; init; }
        public float ThreatLevel { get; init; }
        public float BeliefConfidence { get; init; }
        public StrategicMode Mode { get; init; }
        public bool WasHit { get; init; }
        public bool DealtDamage { get; init; }
    }
}
