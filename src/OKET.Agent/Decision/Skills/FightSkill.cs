using OKET.Core.State;
using OKET.Core.Actions;

namespace OKET.Agent.Decision.Skills;

/// <summary>
/// Combat skill - handles shooting and basic positioning during fights.
/// </summary>
public sealed class FightSkill : SkillBase
{
    public override string Name => "Fight";

    public override IReadOnlySet<StrategicMode> Modes { get; } = new HashSet<StrategicMode>
    {
        StrategicMode.Fight
    };

    private readonly AimSkill _aimSkill = new();
    private int _burstCounter;
    private const int BurstLength = 5;
    private const int BurstCooldown = 3;

    public override ActionPlan Execute(GameState state, StrategicMode mode)
    {
        IsActive = true;
        var actions = new List<GameAction>();

        // Always try to aim
        var aimPlan = _aimSkill.Execute(state, mode);
        actions.AddRange(aimPlan.Actions);

        // Determine if we should shoot
        bool shouldShoot = ShouldShoot(state);

        if (shouldShoot)
        {
            _burstCounter++;

            if (_burstCounter <= BurstLength)
            {
                // Fire
                actions.Add(GameAction.Press(ActionType.Attack));
            }
            else if (_burstCounter > BurstLength + BurstCooldown)
            {
                // Reset burst
                _burstCounter = 0;
            }
            else
            {
                // In burst cooldown - release trigger
                actions.Add(GameAction.Release(ActionType.Attack));
            }
        }
        else
        {
            // Not shooting - release trigger
            actions.Add(GameAction.Release(ActionType.Attack));
            _burstCounter = 0;
        }

        string reason = shouldShoot
            ? $"Engaging target (burst {_burstCounter}/{BurstLength})"
            : "Acquiring target";

        return new ActionPlan
        {
            FrameId = state.FrameId,
            Mode = mode,
            Actions = actions,
            Reason = reason,
            Confidence = state.Aim.Target?.Confidence ?? 0.5f
        };
    }

    private static bool ShouldShoot(GameState state)
    {
        // Don't shoot if no target
        if (state.Aim.Target == null)
            return false;

        // Don't shoot if reloading
        if (state.Hud.IsReloading)
            return false;

        // Don't shoot if no ammo
        if (state.Hud.AmmoClip <= 0)
            return false;

        // Don't shoot if target confidence is too low
        if (state.Aim.Target.Confidence < 0.4f)
            return false;

        // Shoot if on target
        if (state.Aim.IsOnTarget)
            return true;

        // Shoot if close enough to target (within 1.5x tolerance)
        if (state.Aim.PixelDistance < AimState.OnTargetTolerance * 1.5f)
            return true;

        return false;
    }

    public override void Reset()
    {
        base.Reset();
        _aimSkill.Reset();
        _burstCounter = 0;
    }
}
