using OKET.Core.State;
using OKET.Core.Actions;

namespace OKET.Core.Interfaces;

/// <summary>
/// High-level policy that decides what the agent should do.
/// This is the "Manager" in the two-tier architecture.
/// </summary>
public interface IPolicy
{
    /// <summary>
    /// Decide the strategic mode and high-level action given current state.
    /// </summary>
    (StrategicMode Mode, float Confidence) Decide(GameState state);

    /// <summary>Name of this policy for logging.</summary>
    string Name { get; }
}

/// <summary>
/// A skill that executes a specific behavior (the "how").
/// This is the "Skill Controller" in the two-tier architecture.
/// </summary>
public interface ISkill
{
    /// <summary>Name of this skill.</summary>
    string Name { get; }

    /// <summary>Which strategic mode(s) this skill handles.</summary>
    IReadOnlySet<StrategicMode> Modes { get; }

    /// <summary>
    /// Generate actions to execute the skill.
    /// </summary>
    ActionPlan Execute(GameState state, StrategicMode mode);

    /// <summary>Whether the skill is currently active.</summary>
    bool IsActive { get; }

    /// <summary>Reset skill state (e.g., when switching modes).</summary>
    void Reset();
}
