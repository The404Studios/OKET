using OKET.Core.State;
using OKET.Core.Actions;

namespace OKET.Core.Interfaces;

/// <summary>
/// Safety layer that validates and modifies action plans.
/// Prevents self-sabotage and ensures stable behavior.
/// </summary>
public interface ISafetyLayer
{
    /// <summary>
    /// Validate and potentially modify an action plan.
    /// Returns the safe plan (may be different from input).
    /// </summary>
    ActionPlan Validate(ActionPlan plan, GameState state);

    /// <summary>
    /// Check if a specific action is allowed in current state.
    /// </summary>
    bool IsActionAllowed(GameAction action, GameState state);

    /// <summary>Constraints that are currently active.</summary>
    IReadOnlyList<string> ActiveConstraints { get; }
}
