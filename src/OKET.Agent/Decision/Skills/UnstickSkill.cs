using OKET.Core.State;
using OKET.Core.Actions;

namespace OKET.Agent.Decision.Skills;

/// <summary>
/// Recovery skill for when the bot gets stuck.
/// Tries various movement patterns to break free.
/// </summary>
public sealed class UnstickSkill : SkillBase
{
    public override string Name => "Unstick";

    public override IReadOnlySet<StrategicMode> Modes { get; } = new HashSet<StrategicMode>
    {
        StrategicMode.Unstick
    };

    private int _attemptPhase;
    private int _phaseTimer;
    private const int PhaseDuration = 30; // frames per phase

    public override ActionPlan Execute(GameState state, StrategicMode mode)
    {
        IsActive = true;
        var actions = new List<GameAction>();

        _phaseTimer++;
        if (_phaseTimer >= PhaseDuration)
        {
            _phaseTimer = 0;
            _attemptPhase = (_attemptPhase + 1) % 6;
        }

        // Try different recovery patterns
        switch (_attemptPhase)
        {
            case 0: // Back up
                actions.Add(GameAction.Press(ActionType.MoveBackward, 100));
                break;

            case 1: // Turn left and back
                actions.Add(GameAction.MouseMove(-30, 0));
                actions.Add(GameAction.Press(ActionType.MoveBackward, 100));
                break;

            case 2: // Jump and back
                actions.Add(GameAction.Press(ActionType.Jump, 50));
                actions.Add(GameAction.Press(ActionType.MoveBackward, 100));
                break;

            case 3: // Turn right and forward
                actions.Add(GameAction.MouseMove(30, 0));
                actions.Add(GameAction.Press(ActionType.MoveForward, 100));
                break;

            case 4: // Strafe left and jump
                actions.Add(GameAction.Press(ActionType.MoveLeft, 100));
                actions.Add(GameAction.Press(ActionType.Jump, 50));
                break;

            case 5: // Strafe right and jump
                actions.Add(GameAction.Press(ActionType.MoveRight, 100));
                actions.Add(GameAction.Press(ActionType.Jump, 50));
                break;
        }

        return new ActionPlan
        {
            FrameId = state.FrameId,
            Mode = mode,
            Actions = actions,
            Reason = $"Unsticking (phase {_attemptPhase + 1}/6)",
            Confidence = 0.7f,
            Interruptible = true
        };
    }

    public override void Reset()
    {
        base.Reset();
        _attemptPhase = 0;
        _phaseTimer = 0;
    }
}
