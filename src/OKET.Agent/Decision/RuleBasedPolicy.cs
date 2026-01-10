using OKET.Core.State;
using OKET.Core.Actions;
using OKET.Core.Interfaces;

namespace OKET.Agent.Decision;

/// <summary>
/// Rule-based policy for initial behavior.
/// The "Manager" that decides WHAT to do.
/// Can be replaced with a learned policy later.
/// </summary>
public sealed class RuleBasedPolicy : IPolicy
{
    public string Name => "RuleBasedPolicy";

    // State for decision persistence
    private StrategicMode _currentMode = StrategicMode.Idle;
    private int _framesInMode;
    private const int MinFramesBeforeSwitch = 15; // ~0.5 seconds

    public (StrategicMode Mode, float Confidence) Decide(GameState state)
    {
        _framesInMode++;

        // Death override - always idle when dead
        if (state.Hud.IsDead)
        {
            return SetMode(StrategicMode.Idle, 1.0f);
        }

        // Stuck recovery - highest priority
        if (state.IsStuck)
        {
            return SetMode(StrategicMode.Unstick, 0.9f);
        }

        // Critical health - need to kite or heal
        if (state.Hud.IsCriticalHealth && state.ThreatsInFov > 0)
        {
            return SetMode(StrategicMode.Kite, 0.95f);
        }

        // Need reload and no immediate threats
        if (state.Hud.NeedsReload && state.NearestThreatDistance > 300)
        {
            return SetMode(StrategicMode.Reload, 0.8f);
        }

        // Active combat - threats visible
        if (state.ThreatsInFov > 0)
        {
            // Low health - kite while fighting
            if (state.Hud.IsLowHealth)
            {
                return SetMode(StrategicMode.Kite, 0.85f);
            }

            // Have ammo and target - fight
            if (!state.Hud.HasNoAmmo && state.Aim.Target != null)
            {
                return SetMode(StrategicMode.Fight, 0.9f);
            }

            // No ammo - kite to safety
            if (state.Hud.HasNoAmmo)
            {
                return SetMode(StrategicMode.Kite, 0.8f);
            }
        }

        // Low ammo but safe - reload proactively
        if (state.Hud.IsLowAmmo && state.ThreatsInFov == 0)
        {
            return SetMode(StrategicMode.Reload, 0.6f);
        }

        // Nothing urgent - check for barricade repair
        // (would need to detect damaged barricades)

        // Default - idle/patrol
        return SetMode(StrategicMode.Idle, 0.5f);
    }

    private (StrategicMode Mode, float Confidence) SetMode(StrategicMode newMode, float confidence)
    {
        // Hysteresis - don't switch modes too rapidly
        if (newMode != _currentMode)
        {
            // Allow immediate switch for high-priority modes
            if (_framesInMode >= MinFramesBeforeSwitch ||
                newMode == StrategicMode.Kite ||
                newMode == StrategicMode.Unstick ||
                _currentMode == StrategicMode.Idle)
            {
                _currentMode = newMode;
                _framesInMode = 0;
            }
            else
            {
                // Stay in current mode
                return (_currentMode, confidence * 0.8f);
            }
        }

        return (newMode, confidence);
    }
}
