namespace OKET.Core.Operators;

/// <summary>
/// Gate Feedback System - Implements backpropagation of gate signals.
///
/// CORE PRINCIPLE: Every gate operation produces a signal that must propagate
/// back through the system to adjust future behavior.
///
/// Energy Conservation Law:
///   Input Energy + Stored Energy = Output Energy + Dissipated Energy
///
/// For stability (output > input), the system must:
/// 1. Accumulate positive feedback from successful operations
/// 2. Dissipate negative energy through resistance
/// 3. Maintain load balance across all gates
///
/// The feedback loop ensures that:
/// - Resistance adapts to load (prevents oscillation)
/// - Gain stabilizes at sustainable levels (prevents runaway)
/// - Energy flows toward productive outcomes
/// </summary>
public sealed class GateFeedback
{
    // Feedback accumulators per gate type
    private readonly Dictionary<GateType, GateSignalAccumulator> _accumulators = new();

    // Global energy state
    private float _inputEnergy;
    private float _outputEnergy;
    private float _storedEnergy;
    private float _dissipatedEnergy;

    // Resistance and load parameters
    private float _systemResistance = 1.0f;
    private float _systemLoad = 0.0f;
    private float _targetGain = 1.05f;  // Output should exceed input by 5%

    // Adaptive parameters
    private float _adaptationRate = 0.1f;
    private float _dampingFactor = 0.95f;

    // History for trend analysis
    private readonly Queue<EnergySnapshot> _energyHistory = new();
    private const int MaxHistory = 100;

    // Backpropagation gradients
    private readonly Dictionary<GateType, float> _gradients = new();

    /// <summary>
    /// Current system gain (output/input ratio).
    /// > 1.0 means system is producing more than consuming.
    /// </summary>
    public float CurrentGain => _inputEnergy > 0.001f ? _outputEnergy / _inputEnergy : 1.0f;

    /// <summary>
    /// Is the system in stable state (resistance matches load)?
    /// </summary>
    public bool IsStable => Math.Abs(_systemResistance - _systemLoad) < 0.1f && CurrentGain >= 0.95f;

    /// <summary>
    /// Current energy balance (should trend positive for healthy system).
    /// </summary>
    public float EnergyBalance => _storedEnergy + _outputEnergy - _inputEnergy - _dissipatedEnergy;

    /// <summary>
    /// System resistance level.
    /// </summary>
    public float Resistance => _systemResistance;

    /// <summary>
    /// System load level.
    /// </summary>
    public float Load => _systemLoad;

    /// <summary>
    /// Get the backpropagation gradient for a gate type.
    /// Positive = gate should be more permissive.
    /// Negative = gate should be more restrictive.
    /// </summary>
    public float GetGradient(GateType gate) => _gradients.GetValueOrDefault(gate, 0f);

    public GateFeedback()
    {
        // Initialize accumulators for each gate type
        foreach (GateType gate in Enum.GetValues<GateType>())
        {
            _accumulators[gate] = new GateSignalAccumulator();
            _gradients[gate] = 0f;
        }
    }

    /// <summary>
    /// Record a gate operation and its outcome.
    /// This is the forward pass - gate was attempted and produced a result.
    /// </summary>
    public void RecordGateOperation(
        GateType gate,
        bool permitted,
        float confidence,
        float outcomeValue)  // Positive = good outcome, Negative = bad outcome
    {
        var accumulator = _accumulators[gate];

        // Calculate energy contribution
        float energyIn = confidence;  // Input energy is the confidence/effort
        float energyOut = permitted ? outcomeValue * confidence : outcomeValue * 0.3f;

        // Record in accumulator
        accumulator.AddSample(permitted, energyIn, energyOut, outcomeValue);

        // Update global energy state
        _inputEnergy = _inputEnergy * _dampingFactor + energyIn;

        if (permitted && outcomeValue > 0)
        {
            _outputEnergy = _outputEnergy * _dampingFactor + energyOut;
        }
        else
        {
            // Denied or negative outcome - energy is dissipated
            _dissipatedEnergy = _dissipatedEnergy * _dampingFactor + Math.Abs(energyOut);
        }

        // Update load based on this operation
        _systemLoad = _systemLoad * _dampingFactor + (permitted ? confidence : confidence * 0.5f);
    }

    /// <summary>
    /// Record the final outcome of a cognitive cycle.
    /// This closes the loop - we know if our actions were productive.
    /// </summary>
    public void RecordCycleOutcome(
        float survivalScore,     // Did we survive? [0, 1]
        float progressScore,     // Did we make progress? [-1, 1]
        float strainDelta,       // Change in system strain
        Dictionary<GateType, bool> gatesUsed)  // Which gates were used this cycle
    {
        // Calculate cycle energy
        float cycleEnergy = (survivalScore * 0.5f + (progressScore + 1f) * 0.25f) - Math.Abs(strainDelta) * 0.25f;

        // Store energy for future cycles
        _storedEnergy = _storedEnergy * 0.99f + cycleEnergy * 0.01f;

        // Record snapshot
        _energyHistory.Enqueue(new EnergySnapshot
        {
            Timestamp = DateTime.UtcNow,
            Input = _inputEnergy,
            Output = _outputEnergy,
            Stored = _storedEnergy,
            Dissipated = _dissipatedEnergy,
            Gain = CurrentGain
        });

        while (_energyHistory.Count > MaxHistory)
            _energyHistory.Dequeue();

        // === BACKPROPAGATION ===
        // Propagate outcome back through the gates that were used
        ComputeBackpropagation(cycleEnergy, gatesUsed);

        // === RESISTANCE/LOAD BALANCING ===
        AdaptResistance();
    }

    /// <summary>
    /// Compute backpropagation gradients for each gate.
    /// </summary>
    private void ComputeBackpropagation(float cycleEnergy, Dictionary<GateType, bool> gatesUsed)
    {
        foreach (var (gate, wasPermitted) in gatesUsed)
        {
            var accumulator = _accumulators[gate];

            // Compute gradient based on outcome and gate behavior
            // If outcome was good and gate permitted -> reinforce (positive gradient)
            // If outcome was good and gate denied -> gate was too strict (positive gradient)
            // If outcome was bad and gate permitted -> gate was too lenient (negative gradient)
            // If outcome was bad and gate denied -> gate was correct (small positive gradient)

            float gradient;
            if (cycleEnergy > 0)
            {
                // Good outcome
                gradient = wasPermitted ? cycleEnergy * 0.5f : cycleEnergy * 0.3f;
            }
            else
            {
                // Bad outcome
                gradient = wasPermitted ? cycleEnergy * 0.7f : -cycleEnergy * 0.1f;
            }

            // Apply gradient with momentum (exponential moving average)
            _gradients[gate] = _gradients[gate] * 0.9f + gradient * 0.1f;

            // Update accumulator with feedback
            accumulator.ApplyFeedback(gradient);
        }
    }

    /// <summary>
    /// Adapt system resistance to match load and achieve target gain.
    /// </summary>
    private void AdaptResistance()
    {
        // Target: Resistance should match Load for stability
        float loadResistanceError = _systemLoad - _systemResistance;

        // Target: Gain should be at or above target
        float gainError = _targetGain - CurrentGain;

        // Adapt resistance
        // If gain is too low, decrease resistance (let more through)
        // If gain is too high, increase resistance (be more selective)
        // If load > resistance, increase resistance (prevent overload)
        // If load < resistance, decrease resistance (utilize capacity)

        float resistanceAdjustment =
            loadResistanceError * 0.3f +      // Match load
            gainError * 0.5f +                 // Achieve target gain
            (_dissipatedEnergy - _outputEnergy) * 0.2f;  // Minimize waste

        _systemResistance = Math.Clamp(
            _systemResistance + resistanceAdjustment * _adaptationRate,
            0.1f, 3.0f);

        // Decay load toward baseline
        _systemLoad *= 0.98f;
    }

    /// <summary>
    /// Get recommended modulation based on feedback state.
    /// Call this when making gate decisions to incorporate learned feedback.
    /// </summary>
    public GateFeedbackModulation GetModulation(GateType gate)
    {
        var accumulator = _accumulators[gate];
        var gradient = _gradients[gate];

        // Compute permission bias based on accumulated evidence
        float permissionBias = accumulator.PermissionBias;

        // Adjust for current system state
        if (!IsStable)
        {
            // System is unstable - be more conservative
            permissionBias *= 0.7f;
        }

        if (CurrentGain < 0.9f)
        {
            // System is losing energy - be more selective with Emit
            if (gate == GateType.Emit)
                permissionBias -= 0.2f;
            // But more permissive with Activate (need more input)
            if (gate == GateType.Activate)
                permissionBias += 0.1f;
        }

        return new GateFeedbackModulation
        {
            PermissionBias = Math.Clamp(permissionBias, -0.5f, 0.5f),
            Gradient = gradient,
            Confidence = accumulator.Confidence,
            RecommendedThresholdAdjustment = -gradient * 0.1f  // Gradient descent on threshold
        };
    }

    /// <summary>
    /// Reset the feedback system (e.g., on death/respawn).
    /// </summary>
    public void Reset()
    {
        _inputEnergy = 0;
        _outputEnergy = 0;
        _storedEnergy = 0;
        _dissipatedEnergy = 0;
        _systemResistance = 1.0f;
        _systemLoad = 0;

        foreach (var accumulator in _accumulators.Values)
        {
            accumulator.Reset();
        }

        foreach (var gate in _gradients.Keys.ToList())
        {
            _gradients[gate] = 0f;
        }

        _energyHistory.Clear();
    }

    /// <summary>
    /// Get diagnostic information.
    /// </summary>
    public string GetDiagnostics()
    {
        var trend = GetEnergyTrend();

        return $"""
            === GATE FEEDBACK ===
            Energy: in={_inputEnergy:F2}, out={_outputEnergy:F2}, stored={_storedEnergy:F2}
            Gain: {CurrentGain:F2} (target={_targetGain:F2})
            Resistance: {_systemResistance:F2}, Load: {_systemLoad:F2}
            Stable: {IsStable}, Balance: {EnergyBalance:F2}
            Trend: {trend:+0.00;-0.00}

            Gradients:
              Activate: {_gradients[GateType.Activate]:+0.00;-0.00}
              Transform: {_gradients[GateType.Transform]:+0.00;-0.00}
              Emit: {_gradients[GateType.Emit]:+0.00;-0.00}
              Consume: {_gradients[GateType.Consume]:+0.00;-0.00}
              Yield: {_gradients[GateType.Yield]:+0.00;-0.00}
            =====================
            """;
    }

    private float GetEnergyTrend()
    {
        if (_energyHistory.Count < 10)
            return 0f;

        var list = _energyHistory.ToList();
        int mid = list.Count / 2;

        float recentGain = list.Skip(mid).Average(s => s.Gain);
        float oldGain = list.Take(mid).Average(s => s.Gain);

        return recentGain - oldGain;
    }
}

/// <summary>
/// Accumulates gate signal statistics for backpropagation.
/// </summary>
public sealed class GateSignalAccumulator
{
    private int _permitCount;
    private int _denyCount;
    private float _permitEnergySum;
    private float _denyEnergySum;
    private float _outcomeSum;
    private float _feedbackSum;

    /// <summary>
    /// Bias toward permitting this gate [-1, 1].
    /// </summary>
    public float PermissionBias
    {
        get
        {
            int total = _permitCount + _denyCount;
            if (total < 5) return 0f;

            float permitRate = _permitCount / (float)total;
            float energyRatio = _permitEnergySum / Math.Max(0.001f, _permitEnergySum + _denyEnergySum);
            float outcomeBias = _outcomeSum / total;

            return (permitRate - 0.5f) * 0.3f +
                   (energyRatio - 0.5f) * 0.3f +
                   outcomeBias * 0.4f +
                   _feedbackSum * 0.1f;
        }
    }

    /// <summary>
    /// Confidence in the accumulated statistics.
    /// </summary>
    public float Confidence => Math.Min(1f, (_permitCount + _denyCount) / 50f);

    public void AddSample(bool permitted, float energyIn, float energyOut, float outcome)
    {
        if (permitted)
        {
            _permitCount++;
            _permitEnergySum += energyOut;
        }
        else
        {
            _denyCount++;
            _denyEnergySum += Math.Abs(energyOut);
        }
        _outcomeSum = _outcomeSum * 0.99f + outcome * 0.01f;
    }

    public void ApplyFeedback(float gradient)
    {
        _feedbackSum = _feedbackSum * 0.95f + gradient;
    }

    public void Reset()
    {
        _permitCount = 0;
        _denyCount = 0;
        _permitEnergySum = 0;
        _denyEnergySum = 0;
        _outcomeSum = 0;
        _feedbackSum = 0;
    }
}

/// <summary>
/// Feedback modulation for a specific gate.
/// </summary>
public readonly struct GateFeedbackModulation
{
    /// <summary>Bias to add to permission decision [-0.5, 0.5].</summary>
    public float PermissionBias { get; init; }

    /// <summary>Current gradient (direction of optimization).</summary>
    public float Gradient { get; init; }

    /// <summary>Confidence in this modulation [0, 1].</summary>
    public float Confidence { get; init; }

    /// <summary>Recommended threshold adjustment.</summary>
    public float RecommendedThresholdAdjustment { get; init; }
}

/// <summary>
/// Snapshot of energy state for history tracking.
/// </summary>
internal readonly struct EnergySnapshot
{
    public DateTime Timestamp { get; init; }
    public float Input { get; init; }
    public float Output { get; init; }
    public float Stored { get; init; }
    public float Dissipated { get; init; }
    public float Gain { get; init; }
}
