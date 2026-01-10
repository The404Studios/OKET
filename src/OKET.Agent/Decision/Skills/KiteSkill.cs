using OKET.Core.Types;
using OKET.Core.State;
using OKET.Core.Actions;

namespace OKET.Agent.Decision.Skills;

/// <summary>
/// Kiting skill - retreat while fighting.
/// Maintains distance from threats while dealing damage.
/// </summary>
public sealed class KiteSkill : SkillBase
{
    public override string Name => "Kite";

    public override IReadOnlySet<StrategicMode> Modes { get; } = new HashSet<StrategicMode>
    {
        StrategicMode.Kite
    };

    private readonly AimSkill _aimSkill = new();
    private readonly FightSkill _fightSkill = new();

    private int _strafeDirection = 1; // 1 = right, -1 = left
    private int _strafeTimer;
    private const int StrafeInterval = 30; // frames

    public override ActionPlan Execute(GameState state, StrategicMode mode)
    {
        IsActive = true;
        var actions = new List<GameAction>();

        // Update strafe timer
        _strafeTimer++;
        if (_strafeTimer >= StrafeInterval)
        {
            _strafeTimer = 0;
            _strafeDirection *= -1; // Switch direction
        }

        // Movement - always backing up
        actions.Add(GameAction.Press(ActionType.MoveBackward, 100));

        // Strafe in current direction
        if (_strafeDirection > 0)
        {
            actions.Add(GameAction.Press(ActionType.MoveRight, 100));
            actions.Add(GameAction.Release(ActionType.MoveLeft));
        }
        else
        {
            actions.Add(GameAction.Press(ActionType.MoveLeft, 100));
            actions.Add(GameAction.Release(ActionType.MoveRight));
        }

        // Try to fight if we have a target and ammo
        if (state.Aim.Target != null && state.Hud.AmmoClip > 0)
        {
            var fightPlan = _fightSkill.Execute(state, StrategicMode.Fight);
            actions.AddRange(fightPlan.Actions);
        }
        else
        {
            // Just aim
            var aimPlan = _aimSkill.Execute(state, mode);
            actions.AddRange(aimPlan.Actions);
        }

        // Jump occasionally to avoid being easy target
        if (Random.Shared.Next(100) < 3) // ~3% chance per frame
        {
            actions.Add(GameAction.Press(ActionType.Jump, 50));
        }

        return new ActionPlan
        {
            FrameId = state.FrameId,
            Mode = mode,
            Actions = actions,
            Reason = $"Kiting {(state.Hud.IsLowHealth ? "(low HP)" : "")}",
            Confidence = 0.85f
        };
    }

    public override void Reset()
    {
        base.Reset();
        _aimSkill.Reset();
        _fightSkill.Reset();
        _strafeTimer = 0;
    }
}
