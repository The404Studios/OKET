namespace OKET.Core.Operators;

/// <summary>
/// The seven operators through which potential becomes reality.
///
/// These are NOT control flow. They are a process lifecycle.
/// No gate executes unless the correct binding is present.
///
/// This is the law of potential:
/// - Activate: Admit input into process
/// - Consume: Spend resource/attention
/// - Transform: Change representation without loss
/// - Emit: Produce output, release potential outward
/// - Yield: Pause, hold back intentionally
/// - Block: Hard stop, prevent propagation
///
/// Reality must use all seven, or it breaks.
/// </summary>
public enum GateType
{
    /// <summary>
    /// Admit input into process.
    /// Maps to: PerceptionTrust gate, sensor activation.
    /// Requires: Sufficient trust + relevance signal.
    /// </summary>
    Activate,

    /// <summary>
    /// Spend resource or attention.
    /// Maps to: Action execution, focus allocation.
    /// Requires: Committed posture + available capacity.
    /// </summary>
    Consume,

    /// <summary>
    /// Change representation without loss.
    /// Maps to: Fusion, perception → belief, belief → posture.
    /// Requires: Valid input + transformation rules.
    /// </summary>
    Transform,

    /// <summary>
    /// Produce output, release potential outward.
    /// Maps to: ActionPlan execution, state propagation.
    /// Requires: Inherited OR validated Associated state.
    /// </summary>
    Emit,

    /// <summary>
    /// Pause, hold back intentionally.
    /// Maps to: Hesitation, BeliefLock delay, scan behavior.
    /// Prevents premature collapse of potential.
    /// </summary>
    Yield,

    /// <summary>
    /// Hard stop, prevent propagation.
    /// Maps to: Safety veto, NAND inhibition, forced exclusion.
    /// This is the universal safeguard.
    /// </summary>
    Block
}

/// <summary>
/// Context required for a gate to execute.
///
/// No gate executes unless:
/// 1. Binding is correct (state topology)
/// 2. Direction is valid (not violating constraints)
/// 3. Conversation law permits (NAND not triggered)
/// </summary>
public readonly struct GateContext
{
    /// <summary>
    /// Current binding state of the information.
    /// </summary>
    public BindState State { get; init; }

    /// <summary>
    /// Validity signal from interoception [0, 1].
    /// High = posture can carry load.
    /// </summary>
    public float Validity { get; init; }

    /// <summary>
    /// Trust level from feeling [0.5, 1.5].
    /// Modulates activation thresholds.
    /// </summary>
    public float Trust { get; init; }

    /// <summary>
    /// System strain from Z₄ [0, 3+].
    /// High strain = harder to promote state.
    /// </summary>
    public float Strain { get; init; }

    /// <summary>
    /// Whether NAND/inhibition is active.
    /// If true, Emit and Transform are blocked.
    /// </summary>
    public bool Inhibited { get; init; }

    /// <summary>
    /// Outcome trend [-1, 1].
    /// Used for credit assignment and promotion decisions.
    /// </summary>
    public float OutcomeTrend { get; init; }

    /// <summary>
    /// Whether urgency override is active.
    /// Can bypass Yield under extreme pressure.
    /// </summary>
    public bool UrgencyOverride { get; init; }

    /// <summary>
    /// Check if a specific gate type can execute in this context.
    /// </summary>
    public bool CanExecute(GateType gate) => gate switch
    {
        GateType.Activate => !Inhibited && Trust > 0.5f && State != BindState.Absent,
        GateType.Consume => !Inhibited && State.CanEmit() && Validity > 0.3f,
        GateType.Transform => !Inhibited && State != BindState.Absent,
        GateType.Emit => !Inhibited && State.CanEmit() && Validity > 0.35f,
        GateType.Yield => true, // Yield is always allowed
        GateType.Block => true, // Block is always allowed
        _ => false
    };

    /// <summary>
    /// Get the recommended gate given current context.
    /// </summary>
    public GateType RecommendedGate
    {
        get
        {
            // Block takes absolute priority
            if (Inhibited) return GateType.Block;

            // Low validity → yield
            if (Validity < 0.35f && !UrgencyOverride) return GateType.Yield;

            // High strain → yield unless urgent
            if (Strain > 2.0f && !UrgencyOverride) return GateType.Yield;

            // Absent state → nothing can happen
            if (State == BindState.Absent) return GateType.Block;

            // Separate state → can only activate (observe)
            if (State == BindState.Separate) return GateType.Activate;

            // Associated with good validity → can emit conditionally
            if (State == BindState.Associated && Validity > 0.5f) return GateType.Emit;

            // Inherited → full emit authority
            if (State == BindState.Inherited) return GateType.Emit;

            // Default: transform/process
            return GateType.Transform;
        }
    }

    /// <summary>
    /// Compute the "key count" required for this context.
    /// DNA (Inherited) = 2, RNA (Associated) = 1, else = 0.
    /// </summary>
    public int RequiredKeyCount => State switch
    {
        BindState.Inherited => 2,  // Double-key: stability + outcome
        BindState.Associated => 1, // Single-key: current context
        _ => 0
    };

    public override string ToString() =>
        $"Gate[{State}, V={Validity:F2}, T={Trust:F2}, S={Strain:F2}, Inh={Inhibited}] → {RecommendedGate}";
}
