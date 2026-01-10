using OKET.Core.State;
using OKET.Core.Actions;
using OKET.Core.Interfaces;

namespace OKET.Agent.Safety;

/// <summary>
/// Safety layer that validates and constrains action plans.
/// Prevents self-sabotaging behaviors and ensures stable operation.
/// </summary>
public sealed class SafetyLayer : ISafetyLayer
{
    private readonly List<string> _activeConstraints = new();
    private readonly HashSet<ActionType> _blockedActions = new();
    private DateTime _lastActionTime = DateTime.MinValue;
    private int _actionCount;
    private const int MaxActionsPerSecond = 60;

    // Configurable constraints
    public float MinTargetConfidenceForShoot { get; set; } = 0.3f;
    public float MinThreatDistanceForReload { get; set; } = 200f;
    public float MaxMouseSpeed { get; set; } = 100f;
    public int MaxConsecutiveSameAction { get; set; } = 120;

    // Tracking for thrashing detection
    private ActionType _lastActionType;
    private int _sameActionCount;

    public IReadOnlyList<string> ActiveConstraints => _activeConstraints;

    public ActionPlan Validate(ActionPlan plan, GameState state)
    {
        _activeConstraints.Clear();
        var validatedActions = new List<GameAction>();

        foreach (var action in plan.Actions)
        {
            if (IsActionAllowed(action, state))
            {
                var clampedAction = ClampAction(action);
                validatedActions.Add(clampedAction);
            }
        }

        // Rate limiting
        RateLimitActions(validatedActions);

        // Thrashing detection
        DetectThrashing(validatedActions);

        return new ActionPlan
        {
            FrameId = plan.FrameId,
            Mode = plan.Mode,
            Actions = validatedActions,
            Reason = plan.Reason + (ActiveConstraints.Count > 0 ? $" [constraints: {string.Join(", ", ActiveConstraints)}]" : ""),
            Confidence = plan.Confidence,
            Interruptible = plan.Interruptible,
            ValidityMs = plan.ValidityMs
        };
    }

    public bool IsActionAllowed(GameAction action, GameState state)
    {
        // Check blocked actions
        if (_blockedActions.Contains(action.Type))
        {
            _activeConstraints.Add($"{action.Type} blocked");
            return false;
        }

        // Constraint: Don't shoot without confidence
        if (action.Type == ActionType.Attack && action.IsPress)
        {
            if (state.Aim.Target == null || state.Aim.Target.Confidence < MinTargetConfidenceForShoot)
            {
                _activeConstraints.Add("no_target_confidence");
                return false;
            }
        }

        // Constraint: Don't reload if threats are close
        if (action.Type == ActionType.Reload)
        {
            if (state.NearestThreatDistance < MinThreatDistanceForReload && state.Hud.AmmoClip > 5)
            {
                _activeConstraints.Add("threat_too_close_for_reload");
                return false;
            }
        }

        // Constraint: Don't act if dead
        if (state.Hud.IsDead)
        {
            if (action.Type != ActionType.None)
            {
                _activeConstraints.Add("player_dead");
                return false;
            }
        }

        return true;
    }

    private GameAction ClampAction(GameAction action)
    {
        // Clamp mouse movement speed
        if (action.Type == ActionType.MouseMove)
        {
            var delta = action.MouseDelta;
            if (delta.Length > MaxMouseSpeed)
            {
                var clamped = delta.Normalized * MaxMouseSpeed;
                return new GameAction
                {
                    Type = ActionType.MouseMove,
                    MouseDelta = clamped,
                    IsPress = action.IsPress
                };
            }
        }

        return action;
    }

    private void RateLimitActions(List<GameAction> actions)
    {
        var now = DateTime.UtcNow;

        // Reset counter every second
        if ((now - _lastActionTime).TotalSeconds >= 1)
        {
            _actionCount = 0;
            _lastActionTime = now;
        }

        // Check if over limit
        if (_actionCount + actions.Count > MaxActionsPerSecond)
        {
            int allowedCount = Math.Max(0, MaxActionsPerSecond - _actionCount);
            if (actions.Count > allowedCount)
            {
                actions.RemoveRange(allowedCount, actions.Count - allowedCount);
                _activeConstraints.Add("rate_limited");
            }
        }

        _actionCount += actions.Count;
    }

    private void DetectThrashing(List<GameAction> actions)
    {
        // Detect if the same action is repeated too many times (potential stuck loop)
        if (actions.Count > 0)
        {
            var primaryAction = actions.FirstOrDefault(a =>
                a.Type != ActionType.None &&
                a.Type != ActionType.MouseMove);

            if (primaryAction != null)
            {
                if (primaryAction.Type == _lastActionType)
                {
                    _sameActionCount++;

                    if (_sameActionCount > MaxConsecutiveSameAction)
                    {
                        _activeConstraints.Add("action_thrashing_detected");
                        // Could trigger mode change or block action
                    }
                }
                else
                {
                    _lastActionType = primaryAction.Type;
                    _sameActionCount = 1;
                }
            }
        }
    }

    /// <summary>
    /// Temporarily block an action type.
    /// </summary>
    public void BlockAction(ActionType action, TimeSpan duration)
    {
        _blockedActions.Add(action);
        _ = UnblockAfterDelay(action, duration);
    }

    private async Task UnblockAfterDelay(ActionType action, TimeSpan duration)
    {
        await Task.Delay(duration);
        _blockedActions.Remove(action);
    }

    /// <summary>
    /// Unblock all actions.
    /// </summary>
    public void Reset()
    {
        _blockedActions.Clear();
        _activeConstraints.Clear();
        _sameActionCount = 0;
    }
}
