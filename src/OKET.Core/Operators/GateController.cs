namespace OKET.Core.Operators;

using OKET.Core.Integration;

/// <summary>
/// Unified Gate Controller - the central routing authority.
///
/// All information flow is gated through this controller.
/// It ensures:
/// 1. Every operation passes through the correct gate
/// 2. CENTER permission is respected
/// 3. Modulation flows to both sides equally
/// 4. No information escapes validation
///
/// This is the "traffic control" that sits between perception and action.
/// </summary>
public sealed class GateController
{
    private readonly BindingValidator _validator = new();

    // Current gate context (updated each frame)
    private GateContext _context;

    // Gate statistics for diagnostics
    private readonly GateStats _stats = new();

    // Modulation outputs
    private float _perceptionModulation = 1f;
    private float _predictionModulation = 1f;

    /// <summary>
    /// Current gate context.
    /// </summary>
    public GateContext Context => _context;

    /// <summary>
    /// Modulation signal for perception systems (right brain) [0, 1].
    /// Low = be conservative, reduce sensitivity.
    /// High = be aggressive, increase sensitivity.
    /// </summary>
    public float PerceptionModulation => _perceptionModulation;

    /// <summary>
    /// Modulation signal for prediction systems (left brain) [0, 1].
    /// Low = widen patterns, be more tolerant.
    /// High = sharpen patterns, be more precise.
    /// </summary>
    public float PredictionModulation => _predictionModulation;

    /// <summary>
    /// The currently recommended gate operation.
    /// </summary>
    public GateType RecommendedGate => _context.RecommendedGate;

    /// <summary>
    /// Gate execution statistics.
    /// </summary>
    public GateStats Stats => _stats;

    /// <summary>
    /// Update the gate context from current cognitive state.
    /// Call this every frame before routing decisions.
    /// </summary>
    public void UpdateContext(
        BindState bindState,
        float validity,
        float trust,
        float strain,
        bool inhibited,
        float outcomeTrend,
        bool urgencyOverride,
        IntegrationState centerState)
    {
        _context = new GateContext
        {
            State = bindState,
            Validity = validity,
            Trust = trust,
            Strain = strain,
            Inhibited = inhibited,
            OutcomeTrend = outcomeTrend,
            UrgencyOverride = urgencyOverride,
            CenterPermission = centerState.Permission,
            CenterCoherence = centerState.Coherence,
            DirectionViability = centerState.DirectionViability
        };

        // Update modulation signals from CENTER
        _perceptionModulation = centerState.LocalModulation;
        _predictionModulation = centerState.GlobalModulation;
    }

    /// <summary>
    /// Attempt to execute a gate operation.
    /// Returns the result and whether it was permitted.
    /// </summary>
    public GateResult TryGate(GateType gate)
    {
        var validation = _validator.Validate(gate, _context);

        if (validation.Permitted)
        {
            _stats.RecordSuccess(gate);
            return new GateResult
            {
                Gate = gate,
                Permitted = true,
                Reason = validation.Reason,
                Modulation = GetModulationForGate(gate)
            };
        }
        else
        {
            _stats.RecordDenial(gate, validation.Law);
            return new GateResult
            {
                Gate = gate,
                Permitted = false,
                Reason = validation.Reason,
                FallbackGate = GetFallbackGate(gate),
                Modulation = 0f
            };
        }
    }

    /// <summary>
    /// Route an activation request (new input entering system).
    /// </summary>
    public GateResult RouteActivation()
    {
        var result = TryGate(GateType.Activate);
        if (!result.Permitted)
        {
            // Try fallback to just observing
            return new GateResult
            {
                Gate = GateType.Yield,
                Permitted = true,
                Reason = "Activation denied, yielding",
                Modulation = _perceptionModulation * 0.5f
            };
        }
        return result;
    }

    /// <summary>
    /// Route a transformation request (perception → belief, belief → posture).
    /// </summary>
    public GateResult RouteTransform()
    {
        var result = TryGate(GateType.Transform);
        if (!result.Permitted)
        {
            // Low coherence - yield and wait for synchronization
            return new GateResult
            {
                Gate = GateType.Yield,
                Permitted = true,
                Reason = "Transform denied, waiting for coherence",
                Modulation = 0.5f
            };
        }
        return result;
    }

    /// <summary>
    /// Route an emission request (action output).
    /// </summary>
    public GateResult RouteEmission()
    {
        var result = TryGate(GateType.Emit);
        if (!result.Permitted)
        {
            // Check if we can at least yield (defensive posture)
            if (_context.Validity > 0.2f || _context.UrgencyOverride)
            {
                return new GateResult
                {
                    Gate = GateType.Yield,
                    Permitted = true,
                    Reason = "Emit denied, defensive yield",
                    Modulation = _perceptionModulation * 0.3f
                };
            }
            // Block completely
            return new GateResult
            {
                Gate = GateType.Block,
                Permitted = true,
                Reason = "Emit denied, blocking",
                Modulation = 0f
            };
        }
        return result;
    }

    /// <summary>
    /// Route a consumption request (spending resource/attention).
    /// </summary>
    public GateResult RouteConsumption()
    {
        var result = TryGate(GateType.Consume);
        if (!result.Permitted)
        {
            return new GateResult
            {
                Gate = GateType.Yield,
                Permitted = true,
                Reason = "Consume denied, conserving resources",
                Modulation = _predictionModulation * 0.5f
            };
        }
        return result;
    }

    /// <summary>
    /// Get the appropriate modulation for a gate type.
    /// </summary>
    private float GetModulationForGate(GateType gate)
    {
        return gate switch
        {
            GateType.Activate => _perceptionModulation,
            GateType.Transform => (_perceptionModulation + _predictionModulation) / 2f,
            GateType.Emit => _predictionModulation,
            GateType.Consume => _predictionModulation,
            GateType.Yield => Math.Max(_perceptionModulation, _predictionModulation) * 0.7f,
            GateType.Block => 0f,
            _ => 0.5f
        };
    }

    /// <summary>
    /// Get the fallback gate when a gate is denied.
    /// </summary>
    private GateType GetFallbackGate(GateType denied)
    {
        return denied switch
        {
            GateType.Emit => GateType.Yield,
            GateType.Consume => GateType.Yield,
            GateType.Transform => GateType.Yield,
            GateType.Activate => GateType.Yield,
            _ => GateType.Block
        };
    }

    /// <summary>
    /// Check if the system should be in defensive mode.
    /// </summary>
    public bool ShouldBeDefensive =>
        _context.CenterPermission < 0.4f ||
        _context.Validity < 0.4f ||
        _context.Strain > 1.5f;

    /// <summary>
    /// Check if the system can act aggressively.
    /// </summary>
    public bool CanActAggressively =>
        _context.CenterPermission > 0.7f &&
        _context.Validity > 0.6f &&
        _context.State.CanEmit() &&
        _context.DirectionViability > 0.3f;

    /// <summary>
    /// Get comprehensive diagnostics.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"""
            === GATE CONTROLLER ===
            Context: {_context}
            Recommended: {RecommendedGate}
            Modulation: perception={_perceptionModulation:F2}, prediction={_predictionModulation:F2}
            Mode: {(ShouldBeDefensive ? "DEFENSIVE" : CanActAggressively ? "AGGRESSIVE" : "CAUTIOUS")}

            Statistics:
            {_stats.GetSummary()}
            =======================
            """;
    }
}

/// <summary>
/// Result of a gate operation.
/// </summary>
public struct GateResult
{
    /// <summary>The gate that was attempted.</summary>
    public GateType Gate { get; init; }

    /// <summary>Whether the gate was permitted.</summary>
    public bool Permitted { get; init; }

    /// <summary>Reason for the decision.</summary>
    public string Reason { get; init; }

    /// <summary>Fallback gate if denied.</summary>
    public GateType? FallbackGate { get; init; }

    /// <summary>Modulation value to apply [0, 1].</summary>
    public float Modulation { get; init; }

    public override string ToString() =>
        Permitted ? $"PERMIT {Gate} (mod={Modulation:F2})" : $"DENY {Gate} → {FallbackGate}: {Reason}";
}

/// <summary>
/// Statistics about gate execution.
/// </summary>
public sealed class GateStats
{
    private readonly Dictionary<GateType, int> _successes = new();
    private readonly Dictionary<GateType, int> _denials = new();
    private readonly Dictionary<string, int> _denialsByLaw = new();
    private int _totalAttempts;

    public void RecordSuccess(GateType gate)
    {
        _totalAttempts++;
        _successes[gate] = _successes.GetValueOrDefault(gate) + 1;
    }

    public void RecordDenial(GateType gate, string law)
    {
        _totalAttempts++;
        _denials[gate] = _denials.GetValueOrDefault(gate) + 1;
        _denialsByLaw[law] = _denialsByLaw.GetValueOrDefault(law) + 1;
    }

    public float GetSuccessRate(GateType gate)
    {
        int total = _successes.GetValueOrDefault(gate) + _denials.GetValueOrDefault(gate);
        return total > 0 ? _successes.GetValueOrDefault(gate) / (float)total : 1f;
    }

    public string GetSummary()
    {
        if (_totalAttempts == 0) return "  No gate attempts yet";

        var lines = new List<string>();
        lines.Add($"  Total attempts: {_totalAttempts}");

        foreach (var gate in Enum.GetValues<GateType>())
        {
            int s = _successes.GetValueOrDefault(gate);
            int d = _denials.GetValueOrDefault(gate);
            if (s + d > 0)
            {
                lines.Add($"  {gate}: {s}/{s + d} ({GetSuccessRate(gate):P0})");
            }
        }

        if (_denialsByLaw.Count > 0)
        {
            lines.Add("  Denials by law:");
            foreach (var (law, count) in _denialsByLaw.OrderByDescending(x => x.Value))
            {
                lines.Add($"    {law}: {count}");
            }
        }

        return string.Join("\n", lines);
    }

    public void Reset()
    {
        _successes.Clear();
        _denials.Clear();
        _denialsByLaw.Clear();
        _totalAttempts = 0;
    }
}
