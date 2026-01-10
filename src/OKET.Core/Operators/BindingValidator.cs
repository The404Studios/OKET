namespace OKET.Core.Operators;

/// <summary>
/// Validates that gates may execute according to the three laws:
///
/// 1. BINDING LAW: State must permit the operation
/// 2. DIRECTION LAW: Operation must not violate constraints
/// 3. CONVERSATION LAW: NAND/inhibition must not be active
///
/// "No gate may execute unless Sure Binding, Direction, and Conversation Law all permit."
///
/// This is the formal enforcement of the operator algebra.
/// </summary>
public sealed class BindingValidator
{
    /// <summary>
    /// Validate whether a gate operation is permitted.
    /// Returns a validation result with reason if denied.
    /// </summary>
    public ValidationResult Validate(GateType gate, GateContext context)
    {
        // LAW 1: BINDING - State must permit operation
        var bindingResult = ValidateBinding(gate, context);
        if (!bindingResult.Permitted)
            return bindingResult;

        // LAW 2: DIRECTION - Must not violate constraints
        var directionResult = ValidateDirection(gate, context);
        if (!directionResult.Permitted)
            return directionResult;

        // LAW 3: CONVERSATION - NAND/inhibition check
        var conversationResult = ValidateConversation(gate, context);
        if (!conversationResult.Permitted)
            return conversationResult;

        return ValidationResult.Allow(gate, "All laws permit execution");
    }

    /// <summary>
    /// LAW 1: BINDING
    /// State topology must permit the gate type.
    /// </summary>
    private ValidationResult ValidateBinding(GateType gate, GateContext context)
    {
        return gate switch
        {
            // Activate: can activate from any non-Absent state
            GateType.Activate when context.State == BindState.Absent =>
                ValidationResult.Deny(gate, "Binding: Cannot activate Absent state"),

            // Consume: requires Inherited or Associated with sufficient validity
            GateType.Consume when !context.State.CanEmit() =>
                ValidationResult.Deny(gate, "Binding: Cannot consume from non-emitting state"),
            GateType.Consume when context.Validity < 0.3f =>
                ValidationResult.Deny(gate, "Binding: Validity too low to consume"),

            // Transform: allowed from Separate, Associated, Inherited (not Absent)
            GateType.Transform when context.State == BindState.Absent =>
                ValidationResult.Deny(gate, "Binding: Cannot transform Absent state"),

            // Emit: requires Inherited (double-key) or Associated (single-key) with validity
            GateType.Emit when context.State == BindState.Absent =>
                ValidationResult.Deny(gate, "Binding: Cannot emit from Absent state"),
            GateType.Emit when context.State == BindState.Separate =>
                ValidationResult.Deny(gate, "Binding: Cannot emit from Separate state (unbound)"),
            GateType.Emit when context.State == BindState.Associated && context.Validity < 0.35f =>
                ValidationResult.Deny(gate, "Binding: Associated state requires validity > 0.35 to emit"),

            // Yield: always allowed
            GateType.Yield => ValidationResult.Allow(gate, "Binding: Yield always permitted"),

            // Block: always allowed
            GateType.Block => ValidationResult.Allow(gate, "Binding: Block always permitted"),

            _ => ValidationResult.Allow(gate, "Binding: State permits operation")
        };
    }

    /// <summary>
    /// LAW 2: DIRECTION
    /// Operation must not violate directional constraints.
    /// </summary>
    private ValidationResult ValidateDirection(GateType gate, GateContext context)
    {
        return gate switch
        {
            // Emit under high strain without urgency = bad direction
            GateType.Emit when context.Strain > 2.0f && !context.UrgencyOverride =>
                ValidationResult.Deny(gate, "Direction: Cannot emit under high strain without urgency"),

            // Emit with negative outcome trend while already compromised
            GateType.Emit when context.OutcomeTrend < -0.5f && context.Validity < 0.4f =>
                ValidationResult.Deny(gate, "Direction: Declining outcomes with weak validity"),

            // Transform with very low trust = unreliable direction
            GateType.Transform when context.Trust < 0.5f =>
                ValidationResult.Deny(gate, "Direction: Trust too low for reliable transform"),

            // Consume without trust threshold
            GateType.Consume when context.Trust < 0.6f =>
                ValidationResult.Deny(gate, "Direction: Trust too low to consume"),

            // Activate with misaligned outcomes (trying to activate while things declining)
            GateType.Activate when context.OutcomeTrend < -0.7f && context.Strain > 1.5f =>
                ValidationResult.Deny(gate, "Direction: Cannot activate new inputs during crisis"),

            _ => ValidationResult.Allow(gate, "Direction: Operation aligned with constraints")
        };
    }

    /// <summary>
    /// LAW 3: CONVERSATION (NAND / Inhibition)
    /// Certain gate combinations must be prevented.
    /// </summary>
    private ValidationResult ValidateConversation(GateType gate, GateContext context)
    {
        // If inhibited, only Yield and Block are allowed
        if (context.Inhibited)
        {
            return gate switch
            {
                GateType.Yield => ValidationResult.Allow(gate, "Conversation: Yield permitted under inhibition"),
                GateType.Block => ValidationResult.Allow(gate, "Conversation: Block permitted under inhibition"),
                _ => ValidationResult.Deny(gate, $"Conversation: {gate} blocked by NAND inhibition")
            };
        }

        // Double-key requirement for Inherited state modifications
        if (context.State == BindState.Inherited && gate == GateType.Transform)
        {
            // Must have BOTH stability AND positive outcomes
            bool hasStabilityKey = context.Strain < 1.0f;
            bool hasOutcomeKey = context.OutcomeTrend >= -0.1f;

            if (!hasStabilityKey || !hasOutcomeKey)
            {
                return ValidationResult.Deny(gate,
                    $"Conversation: Inherited transform requires double-key (stability={hasStabilityKey}, outcome={hasOutcomeKey})");
            }
        }

        return ValidationResult.Allow(gate, "Conversation: No inhibition active");
    }

    /// <summary>
    /// Compute the gate context from cognitive state.
    /// </summary>
    public static GateContext BuildContext(
        BindState state,
        float validity,
        float trust,
        float strain,
        bool inhibited,
        float outcomeTrend,
        bool urgencyOverride)
    {
        return new GateContext
        {
            State = state,
            Validity = validity,
            Trust = trust,
            Strain = strain,
            Inhibited = inhibited,
            OutcomeTrend = outcomeTrend,
            UrgencyOverride = urgencyOverride
        };
    }
}

/// <summary>
/// Result of gate validation.
/// </summary>
public readonly struct ValidationResult
{
    /// <summary>
    /// Whether the gate is permitted to execute.
    /// </summary>
    public bool Permitted { get; init; }

    /// <summary>
    /// The gate type being validated.
    /// </summary>
    public GateType Gate { get; init; }

    /// <summary>
    /// Reason for the decision.
    /// </summary>
    public string Reason { get; init; }

    /// <summary>
    /// Which law blocked (if denied).
    /// </summary>
    public string Law { get; init; }

    public static ValidationResult Allow(GateType gate, string reason) => new()
    {
        Permitted = true,
        Gate = gate,
        Reason = reason,
        Law = ""
    };

    public static ValidationResult Deny(GateType gate, string reason)
    {
        var law = reason.Split(':')[0];
        return new ValidationResult
        {
            Permitted = false,
            Gate = gate,
            Reason = reason,
            Law = law
        };
    }

    public override string ToString() =>
        Permitted ? $"ALLOW {Gate}: {Reason}" : $"DENY {Gate}: {Reason}";
}
