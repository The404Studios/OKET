using OKET.Core.State;
using OKET.Core.Actions;
using OKET.Core.Interfaces;

namespace OKET.Agent.Decision.Skills;

/// <summary>
/// Base class for skill implementations.
/// </summary>
public abstract class SkillBase : ISkill
{
    public abstract string Name { get; }
    public abstract IReadOnlySet<StrategicMode> Modes { get; }
    public bool IsActive { get; protected set; }

    public abstract ActionPlan Execute(GameState state, StrategicMode mode);

    public virtual void Reset()
    {
        IsActive = false;
    }

    /// <summary>
    /// Create an action plan with the given actions.
    /// </summary>
    protected ActionPlan CreatePlan(GameState state, StrategicMode mode, string reason, params GameAction[] actions)
    {
        return new ActionPlan
        {
            FrameId = state.FrameId,
            Mode = mode,
            Actions = actions.ToList(),
            Reason = reason,
            Confidence = 0.9f
        };
    }
}
