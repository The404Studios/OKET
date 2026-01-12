namespace OKET.Core.Operators;

/// <summary>
/// Pipeline Gate - Universal gate for any stage of the processing pipeline.
///
/// ARCHITECTURE: Every stage has gates on input AND output.
///
///   [Previous Stage] → INPUT GATE → [Processing] → OUTPUT GATE → [Next Stage]
///                           ↑                           ↓
///                           └──── BACKPROPAGATION ──────┘
///
/// Gates control:
/// 1. WHETHER data flows (permit/block)
/// 2. HOW MUCH flows (modulation 0-1)
/// 3. WHAT PRIORITY (urgency ordering)
/// 4. WHAT LEARNS (backpropagation gradient)
/// </summary>
public sealed class PipelineGate
{
    private readonly string _stageName;
    private readonly GateChannel[] _channels;
    private readonly PipelineGateFeedback _feedback = new();

    // Global stage state
    private float _inputEnergy;
    private float _outputEnergy;
    private float _stageGain = 1f;
    private bool _isActive = true;

    /// <summary>Name of the pipeline stage.</summary>
    public string StageName => _stageName;

    /// <summary>Current stage gain (output/input).</summary>
    public float StageGain => _stageGain;

    /// <summary>Is this stage currently active?</summary>
    public bool IsActive => _isActive;

    /// <summary>All channels in this gate.</summary>
    public IReadOnlyList<GateChannel> Channels => _channels;

    /// <summary>Feedback system for learning.</summary>
    public PipelineGateFeedback Feedback => _feedback;

    public PipelineGate(string stageName, params string[] channelNames)
    {
        _stageName = stageName;
        _channels = channelNames.Select(n => new GateChannel(n)).ToArray();
    }

    /// <summary>
    /// Gate an input signal for a specific channel.
    /// </summary>
    public GatedSignal GateInput(string channelName, float signal, float urgency = 0.5f)
    {
        var channel = GetChannel(channelName);
        if (channel == null)
            return new GatedSignal { Signal = 0, Permitted = false, Reason = "Unknown channel" };

        // Apply input gate
        var result = channel.ApplyInputGate(signal, urgency);
        _inputEnergy += result.Permitted ? signal * result.Modulation : 0;

        return result;
    }

    /// <summary>
    /// Gate an output signal for a specific channel.
    /// </summary>
    public GatedSignal GateOutput(string channelName, float signal, float quality = 0.5f)
    {
        var channel = GetChannel(channelName);
        if (channel == null)
            return new GatedSignal { Signal = 0, Permitted = false, Reason = "Unknown channel" };

        // Apply output gate
        var result = channel.ApplyOutputGate(signal, quality);
        _outputEnergy += result.Permitted ? signal * result.Modulation : 0;

        return result;
    }

    /// <summary>
    /// Gate a batch of inputs efficiently.
    /// </summary>
    public Dictionary<string, GatedSignal> GateInputBatch(Dictionary<string, float> signals, float urgency = 0.5f)
    {
        var results = new Dictionary<string, GatedSignal>();
        foreach (var (name, signal) in signals)
        {
            results[name] = GateInput(name, signal, urgency);
        }
        return results;
    }

    /// <summary>
    /// Record outcome for backpropagation.
    /// Call this after the stage has processed and we know the result.
    /// </summary>
    public void RecordOutcome(float successScore, float errorSignal = 0f)
    {
        // Update gain
        _stageGain = _inputEnergy > 0.01f ? _outputEnergy / _inputEnergy : 1f;

        // Propagate to all channels
        foreach (var channel in _channels)
        {
            channel.ApplyBackpropagation(successScore, errorSignal, _stageGain);
        }

        // Update feedback system
        _feedback.RecordCycle(_inputEnergy, _outputEnergy, successScore, errorSignal);

        // Reset energy accumulators for next cycle
        _inputEnergy *= 0.9f;
        _outputEnergy *= 0.9f;
    }

    /// <summary>
    /// Set stage active state.
    /// </summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        foreach (var channel in _channels)
        {
            channel.SetEnabled(active);
        }
    }

    /// <summary>
    /// Get modulation recommendation for downstream stages.
    /// </summary>
    public float GetDownstreamModulation()
    {
        return _feedback.GetDownstreamModulation();
    }

    /// <summary>
    /// Get error gradient for upstream stages (backpropagation).
    /// </summary>
    public float GetUpstreamGradient()
    {
        return _feedback.GetUpstreamGradient();
    }

    private GateChannel? GetChannel(string name)
    {
        return _channels.FirstOrDefault(c => c.Name == name);
    }

    /// <summary>
    /// Get diagnostics for this gate.
    /// </summary>
    public string GetDiagnostics()
    {
        var channelInfo = string.Join("\n", _channels.Select(c => $"    {c.GetSummary()}"));
        return $"""
            === PIPELINE GATE: {_stageName} ===
            Active: {_isActive}, Gain: {_stageGain:F2}
            Input: {_inputEnergy:F2}, Output: {_outputEnergy:F2}
            Channels:
            {channelInfo}
            {_feedback.GetSummary()}
            ===================================
            """;
    }
}

/// <summary>
/// A single channel within a pipeline gate.
/// Each channel has independent input and output gates.
/// </summary>
public sealed class GateChannel
{
    private readonly string _name;

    // Input gate state
    private float _inputThreshold = 0.1f;
    private float _inputModulation = 1f;

    // Output gate state
    private float _outputThreshold = 0.2f;
    private float _outputModulation = 1f;

    // Learning state
    private float _inputBias;
    private float _outputBias;
    private float _gradient;
    private int _permitCount;
    private int _denyCount;
    private float _successAccumulator;
    private bool _isEnabled = true;

    public string Name => _name;
    public float InputModulation => _inputModulation;
    public float OutputModulation => _outputModulation;
    public float SuccessRate => (_permitCount + _denyCount) > 0
        ? _successAccumulator / (_permitCount + _denyCount)
        : 0.5f;

    public GateChannel(string name)
    {
        _name = name;
    }

    public GatedSignal ApplyInputGate(float signal, float urgency)
    {
        if (!_isEnabled)
            return new GatedSignal { Signal = 0, Permitted = false, Reason = "Channel disabled" };

        // Apply threshold with bias
        float effectiveThreshold = Math.Max(0, _inputThreshold - _inputBias - urgency * 0.2f);

        if (signal < effectiveThreshold)
        {
            _denyCount++;
            return new GatedSignal
            {
                Signal = 0,
                Permitted = false,
                Modulation = 0,
                Reason = $"Below threshold ({signal:F2} < {effectiveThreshold:F2})"
            };
        }

        _permitCount++;
        float modulated = signal * _inputModulation;
        return new GatedSignal
        {
            Signal = modulated,
            Permitted = true,
            Modulation = _inputModulation,
            Reason = "Permitted"
        };
    }

    public GatedSignal ApplyOutputGate(float signal, float quality)
    {
        if (!_isEnabled)
            return new GatedSignal { Signal = 0, Permitted = false, Reason = "Channel disabled" };

        // Apply threshold with bias and quality adjustment
        float effectiveThreshold = Math.Max(0, _outputThreshold - _outputBias - quality * 0.1f);

        if (signal < effectiveThreshold)
        {
            return new GatedSignal
            {
                Signal = 0,
                Permitted = false,
                Modulation = 0,
                Reason = $"Quality too low ({signal:F2} < {effectiveThreshold:F2})"
            };
        }

        float modulated = signal * _outputModulation;
        return new GatedSignal
        {
            Signal = modulated,
            Permitted = true,
            Modulation = _outputModulation,
            Reason = "Permitted"
        };
    }

    public void ApplyBackpropagation(float success, float error, float stageGain)
    {
        _successAccumulator = _successAccumulator * 0.95f + success * 0.05f;

        // Compute gradient
        _gradient = _gradient * 0.9f + (success - 0.5f) * (1f + error) * 0.1f;

        // Adjust biases based on gradient
        if (_gradient > 0.1f)
        {
            // Success trending - can be more permissive
            _inputBias = Math.Min(_inputBias + 0.01f, 0.3f);
            _outputBias = Math.Min(_outputBias + 0.01f, 0.3f);
        }
        else if (_gradient < -0.1f)
        {
            // Failure trending - be more restrictive
            _inputBias = Math.Max(_inputBias - 0.01f, -0.2f);
            _outputBias = Math.Max(_outputBias - 0.01f, -0.2f);
        }

        // Adjust modulation based on stage gain
        if (stageGain > 1.1f)
        {
            // Stage is amplifying too much - reduce modulation
            _inputModulation = Math.Max(_inputModulation * 0.99f, 0.3f);
            _outputModulation = Math.Max(_outputModulation * 0.99f, 0.3f);
        }
        else if (stageGain < 0.9f)
        {
            // Stage is attenuating - increase modulation
            _inputModulation = Math.Min(_inputModulation * 1.01f, 1.5f);
            _outputModulation = Math.Min(_outputModulation * 1.01f, 1.5f);
        }
    }

    public void SetEnabled(bool enabled) => _isEnabled = enabled;

    public string GetSummary()
    {
        return $"{_name}: in={_inputModulation:F2}(b={_inputBias:+0.00;-0.00}) " +
               $"out={_outputModulation:F2}(b={_outputBias:+0.00;-0.00}) " +
               $"grad={_gradient:+0.00;-0.00} rate={SuccessRate:P0}";
    }
}

/// <summary>
/// Result of gating a signal.
/// </summary>
public readonly struct GatedSignal
{
    /// <summary>The gated signal value (0 if blocked).</summary>
    public float Signal { get; init; }

    /// <summary>Was the signal permitted through?</summary>
    public bool Permitted { get; init; }

    /// <summary>Modulation applied [0, 1+].</summary>
    public float Modulation { get; init; }

    /// <summary>Reason for decision.</summary>
    public string Reason { get; init; }
}

/// <summary>
/// Feedback system for pipeline gate learning.
/// </summary>
public sealed class PipelineGateFeedback
{
    private float _cumulativeInput;
    private float _cumulativeOutput;
    private float _cumulativeSuccess;
    private float _errorGradient;
    private int _cycleCount;

    public float AverageGain => _cycleCount > 0 && _cumulativeInput > 0.01f
        ? _cumulativeOutput / _cumulativeInput
        : 1f;

    public float ErrorGradient => _errorGradient;

    public void RecordCycle(float input, float output, float success, float error)
    {
        _cumulativeInput = _cumulativeInput * 0.95f + input;
        _cumulativeOutput = _cumulativeOutput * 0.95f + output;
        _cumulativeSuccess = _cumulativeSuccess * 0.95f + success;
        _errorGradient = _errorGradient * 0.9f + error * 0.1f;
        _cycleCount++;
    }

    public float GetDownstreamModulation()
    {
        // Recommend modulation based on our success and gain
        float successFactor = _cumulativeSuccess / Math.Max(1, _cycleCount) * 0.1f;
        float gainFactor = Math.Clamp(AverageGain, 0.5f, 1.5f);
        return Math.Clamp(gainFactor * (0.8f + successFactor), 0.3f, 1.2f);
    }

    public float GetUpstreamGradient()
    {
        // Error signal to propagate backward
        return _errorGradient;
    }

    public string GetSummary()
    {
        return $"Feedback: gain={AverageGain:F2}, error={_errorGradient:+0.00;-0.00}, cycles={_cycleCount}";
    }
}
