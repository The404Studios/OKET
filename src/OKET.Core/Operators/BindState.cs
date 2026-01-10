namespace OKET.Core.Operators;

/// <summary>
/// The four-state topology of information binding.
///
/// This is NOT a boolean. It is a lattice of potential expression:
/// - Inherited: Carried forward intact, trusted by default (prior beliefs, world model)
/// - Associated: Temporarily bound, contextual (candidate beliefs, cue firings)
/// - Separate: Observed but unbound, not trusted (raw percepts, conflicting evidence)
/// - Absent: Explicitly excluded, gated out (safety vetoes, NAND inhibition)
///
/// Information moves through these states as it passes through the crucible.
/// Only information that survives the sink gets promoted from Associated to Inherited.
/// </summary>
public enum BindState
{
    /// <summary>
    /// Potential carried forward intact.
    /// Source → descendant. Prior → default. Memory → baseline.
    /// This is information that has PROVEN it can carry load.
    /// </summary>
    Inherited,

    /// <summary>
    /// Potential temporarily bound.
    /// Contextual coupling. Candidate linkage. "Might belong together."
    /// Association is adjacency, not truth.
    /// </summary>
    Associated,

    /// <summary>
    /// Potential observed but unbound.
    /// Seen but not trusted. Available but not engaged.
    /// Separation preserves potential without contamination.
    /// </summary>
    Separate,

    /// <summary>
    /// Potential explicitly excluded.
    /// Gated out. Not allowed to propagate.
    /// Absence is not destruction — it is containment.
    /// </summary>
    Absent
}

/// <summary>
/// Extension methods for BindState transitions.
/// </summary>
public static class BindStateExtensions
{
    /// <summary>
    /// Can this state be promoted (moved toward Inherited)?
    /// </summary>
    public static bool CanPromote(this BindState state) => state switch
    {
        BindState.Separate => true,    // Separate → Associated
        BindState.Associated => true,  // Associated → Inherited
        _ => false
    };

    /// <summary>
    /// Can this state be demoted (moved toward Absent)?
    /// </summary>
    public static bool CanDemote(this BindState state) => state switch
    {
        BindState.Inherited => true,   // Inherited → Associated (under strain)
        BindState.Associated => true,  // Associated → Separate (validation failed)
        BindState.Separate => true,    // Separate → Absent (explicitly excluded)
        _ => false
    };

    /// <summary>
    /// Get the next state if promoted.
    /// </summary>
    public static BindState Promote(this BindState state) => state switch
    {
        BindState.Separate => BindState.Associated,
        BindState.Associated => BindState.Inherited,
        _ => state
    };

    /// <summary>
    /// Get the next state if demoted.
    /// </summary>
    public static BindState Demote(this BindState state) => state switch
    {
        BindState.Inherited => BindState.Associated,
        BindState.Associated => BindState.Separate,
        BindState.Separate => BindState.Absent,
        _ => state
    };

    /// <summary>
    /// Does this state allow action/emission?
    /// Only Inherited and Associated can emit (with different requirements).
    /// </summary>
    public static bool CanEmit(this BindState state) => state switch
    {
        BindState.Inherited => true,   // Full authority
        BindState.Associated => true,  // Conditional authority
        _ => false
    };

    /// <summary>
    /// Does this state require double-key validation (like DNA)?
    /// Inherited requires dual validation to modify.
    /// </summary>
    public static bool RequiresDoubleKey(this BindState state) =>
        state == BindState.Inherited;

    /// <summary>
    /// Does this state require single-key validation (like RNA)?
    /// Associated only needs current context validation.
    /// </summary>
    public static bool RequiresSingleKey(this BindState state) =>
        state == BindState.Associated;
}
