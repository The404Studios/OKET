using OKET.Core.State;
using OKET.Core.Actions;
using OKET.Core.Interfaces;
using OKET.Agent.Decision.Skills;

namespace OKET.Agent.Decision;

/// <summary>
/// Coordinates skill execution based on the manager's decisions.
/// Routes strategic modes to appropriate skills.
/// </summary>
public sealed class SkillExecutor
{
    private readonly Dictionary<StrategicMode, ISkill> _skills = new();
    private ISkill? _activeSkill;
    private StrategicMode _lastMode = StrategicMode.Idle;

    public SkillExecutor()
    {
        // Register skills
        RegisterSkill(new FightSkill());
        RegisterSkill(new KiteSkill());
        RegisterSkill(new ReloadSkill());
        RegisterSkill(new UnstickSkill());
        RegisterSkill(new AimSkill());
    }

    private void RegisterSkill(ISkill skill)
    {
        foreach (var mode in skill.Modes)
        {
            _skills[mode] = skill;
        }
    }

    /// <summary>
    /// Execute skills for the given mode.
    /// </summary>
    public ActionPlan Execute(GameState state, StrategicMode mode)
    {
        // Mode change - reset old skill
        if (mode != _lastMode)
        {
            _activeSkill?.Reset();
            _lastMode = mode;
        }

        // Find skill for this mode
        if (_skills.TryGetValue(mode, out var skill))
        {
            _activeSkill = skill;
            return skill.Execute(state, mode);
        }

        // No skill for this mode - idle
        _activeSkill = null;
        return CreateIdlePlan(state);
    }

    private static ActionPlan CreateIdlePlan(GameState state)
    {
        return new ActionPlan
        {
            FrameId = state.FrameId,
            Mode = StrategicMode.Idle,
            Actions = new List<GameAction>
            {
                // Release all movement
                GameAction.Release(ActionType.MoveForward),
                GameAction.Release(ActionType.MoveBackward),
                GameAction.Release(ActionType.MoveLeft),
                GameAction.Release(ActionType.MoveRight),
                GameAction.Release(ActionType.Attack)
            },
            Reason = "Idle",
            Confidence = 1.0f
        };
    }

    /// <summary>
    /// Get the currently active skill.
    /// </summary>
    public ISkill? ActiveSkill => _activeSkill;
}
