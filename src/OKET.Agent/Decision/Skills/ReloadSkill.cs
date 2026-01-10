using OKET.Core.State;
using OKET.Core.Actions;

namespace OKET.Agent.Decision.Skills;

/// <summary>
/// Reload skill - safely reload weapon.
/// </summary>
public sealed class ReloadSkill : SkillBase
{
    public override string Name => "Reload";

    public override IReadOnlySet<StrategicMode> Modes { get; } = new HashSet<StrategicMode>
    {
        StrategicMode.Reload
    };

    private bool _reloadInitiated;
    private int _framesSinceReload;

    public override ActionPlan Execute(GameState state, StrategicMode mode)
    {
        IsActive = true;
        var actions = new List<GameAction>();

        // If already reloading, just wait
        if (state.Hud.IsReloading)
        {
            _framesSinceReload++;
            return CreatePlan(state, mode, "Reloading...");
        }

        // Check if we actually need to reload
        if (state.Hud.AmmoClip >= 30 || state.Hud.AmmoReserve <= 0)
        {
            // Don't need to reload
            _reloadInitiated = false;
            return ActionPlan.Empty(state.FrameId);
        }

        // Initiate reload
        if (!_reloadInitiated)
        {
            actions.Add(GameAction.Press(ActionType.Reload, 100));
            _reloadInitiated = true;
            _framesSinceReload = 0;
        }

        // Safety: if reload hasn't started after pressing R, abort
        if (_framesSinceReload > 30 && !state.Hud.IsReloading)
        {
            _reloadInitiated = false;
        }

        _framesSinceReload++;

        return new ActionPlan
        {
            FrameId = state.FrameId,
            Mode = mode,
            Actions = actions,
            Reason = "Initiating reload",
            Confidence = 0.9f,
            Interruptible = true // Can interrupt reload for emergencies
        };
    }

    public override void Reset()
    {
        base.Reset();
        _reloadInitiated = false;
        _framesSinceReload = 0;
    }
}
