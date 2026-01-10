using OKET.Core.Types;
using OKET.Core.State;
using OKET.Core.Actions;

namespace OKET.Agent.Decision.Skills;

/// <summary>
/// Skill for smooth aiming at targets.
/// Handles aim interpolation and tracking.
/// </summary>
public sealed class AimSkill : SkillBase
{
    public override string Name => "Aim";

    public override IReadOnlySet<StrategicMode> Modes { get; } = new HashSet<StrategicMode>
    {
        StrategicMode.Fight,
        StrategicMode.Kite
    };

    // Aim parameters
    private float _sensitivity = 1.0f;
    private float _smoothing = 0.5f;
    private float _maxSpeed = 50f;

    // State
    private Vector2 _lastAimDelta;

    public float Sensitivity
    {
        get => _sensitivity;
        set => _sensitivity = Math.Clamp(value, 0.1f, 5f);
    }

    public float Smoothing
    {
        get => _smoothing;
        set => _smoothing = Math.Clamp(value, 0f, 1f);
    }

    public override ActionPlan Execute(GameState state, StrategicMode mode)
    {
        IsActive = true;

        if (state.Aim.Target == null)
        {
            _lastAimDelta = Vector2.Zero;
            return ActionPlan.Empty(state.FrameId);
        }

        // Calculate required mouse movement
        var offset = state.Aim.OffsetToTarget;

        // Apply sensitivity and smoothing
        var rawDelta = offset * _sensitivity * 0.1f;

        // Smooth with previous delta to avoid jerky movement
        var smoothedDelta = Vector2.Lerp(_lastAimDelta, rawDelta, 1f - _smoothing);

        // Clamp speed
        if (smoothedDelta.Length > _maxSpeed)
        {
            smoothedDelta = smoothedDelta.Normalized * _maxSpeed;
        }

        _lastAimDelta = smoothedDelta;

        // If we're very close, use smaller movements
        if (offset.Length < 30)
        {
            smoothedDelta = smoothedDelta * 0.5f;
        }

        // Don't move if already on target
        if (state.Aim.IsOnTarget)
        {
            return ActionPlan.Empty(state.FrameId);
        }

        var action = GameAction.MouseMove(smoothedDelta.X, smoothedDelta.Y);

        return CreatePlan(state, mode, $"Aiming at target: offset={offset.Length:F0}px", action);
    }

    public override void Reset()
    {
        base.Reset();
        _lastAimDelta = Vector2.Zero;
    }
}
