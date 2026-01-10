using OKET.Core.State;
using OKET.Core.Actions;
using OKET.Core.Cognition;

namespace OKET.Agent.Cognition;

/// <summary>
/// Computes interoceptive (feeling) state from Z-scores and observations.
///
/// INPUTS (from Z-score stack):
///   - Z₁ (perceptual agreement)
///   - Z₂ (belief stability)
///   - Z₃ (control efficacy)
///   - Z₄ (system strain) ← THIS IS THE KEY INPUT
///
/// OUTPUTS (control knobs):
///   - PerceptionTrust
///   - CommitmentConfidence
///   - ActionSpeedModifier
///   - LearningRateModifier
///   - ShouldHesitate / MustActNow gates
/// </summary>
public sealed class InteroceptiveProcessor
{
    // History for computing trends
    private readonly Queue<HistoryEntry> _history = new();
    private const int HistorySize = 60; // ~2 seconds at 30fps

    // Running statistics
    private readonly ExponentialStatistics _predictionErrorStats = new(0.05);
    private readonly ExponentialStatistics _controlSuccessStats = new(0.05);
    private readonly ExponentialStatistics _threatLevelStats = new(0.05);

    // Predictions for error calculation
    private float _predictedThreatLevel;
    private float _predictedHealth;
    private bool _predictedHit;

    // Action tracking
    private int _shotsFired;
    private int _hitsConfirmed;
    private StrategicMode _lastMode;
    private int _modeChanges;

    /// <summary>
    /// Process interoceptive state with Z-scores as explicit inputs.
    /// </summary>
    public InteroceptiveState Process(
        GameState gameState,
        BeliefState belief,
        ActionPlan? lastPlan,
        float systemStrain,        // Z₄
        float z1Agreement,         // Z₁
        float z2BeliefVolatility,  // Z₂
        float z3ControlEfficacy)   // Z₃
    {
        // Record history
        RecordHistory(gameState, belief, lastPlan);

        // Calculate interoceptive measurements
        float predictionError = CalculatePredictionError(gameState, belief);
        float threatPressure = CalculateThreatPressure(gameState, belief);
        float controlConfidence = CalculateControlConfidence(gameState, belief, lastPlan, z3ControlEfficacy);
        float outcomeTrend = CalculateOutcomeTrend();
        float beliefStability = CalculateBeliefStability(z2BeliefVolatility);
        float actionCoherence = CalculateActionCoherence(lastPlan);

        // Sensory alignment from Z₁ (convert Z-score to 0-1 range)
        float sensoryAlignment = Math.Clamp(0.5f + z1Agreement * 0.25f, 0f, 1f);

        // Update predictions for next frame
        UpdatePredictions(gameState, belief, lastPlan);

        return new InteroceptiveState
        {
            // Raw measurements
            PredictionError = predictionError,
            ThreatPressure = threatPressure,
            ControlConfidence = controlConfidence,
            SensoryAlignment = sensoryAlignment,
            OutcomeTrend = outcomeTrend,
            BeliefStability = beliefStability,
            ActionCoherence = actionCoherence,

            // Z₄ as direct input - this is the key architectural fix
            SystemStrain = systemStrain
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
        float error = 0;

        // Threat level prediction error
        float threatError = Math.Abs(belief.ThreatLevel - _predictedThreatLevel);
        error += threatError * 0.3f;

        // Health prediction error
        float healthError = Math.Abs(state.Hud.Health - _predictedHealth) / 100f;
        error += healthError * 0.4f;

        // Hit prediction error
        if (_predictedHit != belief.HitConfirmed)
        {
            error += 0.3f;
        }

        // Normalize with running stats
        _predictionErrorStats.Add(error);
        float normalizedError = (float)Math.Clamp(
            error / Math.Max(_predictionErrorStats.Mean + _predictionErrorStats.StdDev * 2, 0.1),
            0, 1);

        return normalizedError;
    }

    private float CalculateThreatPressure(GameState state, BeliefState belief)
    {
        float currentThreat = belief.ThreatLevel;

        // Threat trend
        float threatTrend = 0;
        if (_history.Count >= 10)
        {
            var recent = _history.TakeLast(10).ToList();
            var older = _history.Take(Math.Max(1, _history.Count - 10)).ToList();

            float recentAvg = recent.Average(h => h.ThreatLevel);
            float olderAvg = older.Average(h => h.ThreatLevel);
            threatTrend = recentAvg - olderAvg;
        }

        // Health pressure
        float healthPressure = 1f - state.Hud.Health / 100f;

        float pressure = currentThreat * 0.4f +
                         Math.Max(0, threatTrend) * 0.3f +
                         healthPressure * 0.3f;

        _threatLevelStats.Add(pressure);

        return Math.Clamp(pressure, 0f, 1f);
    }

    private float CalculateControlConfidence(GameState state, BeliefState belief, ActionPlan? plan, float z3Control)
    {
        // Track shot effectiveness
        if (plan != null && plan.Actions.Any(a => a.Type == ActionType.Attack && a.IsPress))
        {
            _shotsFired++;
        }

        if (belief.HitConfirmed)
        {
            _hitsConfirmed++;
        }

        // Calculate hit rate
        float hitRate = _shotsFired > 0 ? (float)_hitsConfirmed / _shotsFired : 0.5f;

        // Movement effectiveness
        float movementEffectiveness = state.IsStuck ? 0f : 1f;

        // Survival score
        float survivalScore = state.Hud.IsDead ? 0f : 1f;

        // Combine with Z₃ input
        float control = hitRate * 0.3f +
                        movementEffectiveness * 0.2f +
                        survivalScore * 0.2f +
                        Math.Clamp(0.5f + z3Control * 0.2f, 0f, 1f) * 0.3f;

        _controlSuccessStats.Add(control);

        // Decay counters
        if (_shotsFired > 100)
        {
            _shotsFired /= 2;
            _hitsConfirmed /= 2;
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

        float healthTrend = (recentHealth - olderHealth) / 50f;

        float recentThreat = entries.Skip(midpoint).Average(h => h.ThreatLevel);
        float olderThreat = entries.Take(midpoint).Average(h => h.ThreatLevel);

        float threatTrend = olderThreat - recentThreat;

        return Math.Clamp(healthTrend * 0.6f + threatTrend * 0.4f, -1f, 1f);
    }

    private float CalculateBeliefStability(float z2Volatility)
    {
        if (_history.Count < 10) return 0.5f;

        var entries = _history.ToList();

        // Variance in confidence
        float avgConfidence = entries.Average(h => h.BeliefConfidence);
        float variance = entries.Average(h =>
            (h.BeliefConfidence - avgConfidence) * (h.BeliefConfidence - avgConfidence));

        // Mode change rate
        int modeChanges = 0;
        for (int i = 1; i < entries.Count; i++)
        {
            if (entries[i].Mode != entries[i - 1].Mode)
                modeChanges++;
        }
        float changeRate = modeChanges / (float)entries.Count;

        // Combine with Z₂ input
        float instability = variance * 1.5f +
                            changeRate * 0.3f +
                            Math.Max(0, z2Volatility) * 0.2f;

        return Math.Clamp(1f - instability, 0f, 1f);
    }

    private float CalculateActionCoherence(ActionPlan? plan)
    {
        if (plan == null) return 0.5f;

        if (plan.Mode != _lastMode)
        {
            _modeChanges++;
            _lastMode = plan.Mode;
        }

        // Decay
        if (_history.Count > 0 && _history.Count % 30 == 0)
        {
            _modeChanges = Math.Max(0, _modeChanges - 1);
        }

        float coherence = plan.Confidence * 0.6f +
                          (1f - Math.Min(_modeChanges / 10f, 1f)) * 0.4f;

        return Math.Clamp(coherence, 0f, 1f);
    }

    private void UpdatePredictions(GameState state, BeliefState belief, ActionPlan? plan)
    {
        _predictedThreatLevel = belief.ThreatLevel;

        _predictedHealth = state.Hud.Health;
        if (belief.IsUnderAttack)
        {
            _predictedHealth = Math.Max(0, state.Hud.Health - 5);
        }

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
