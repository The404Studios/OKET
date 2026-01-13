namespace OKET.Core.Actions;

/// <summary>
/// Primitive action types the agent can execute.
/// </summary>
public enum ActionType
{
    None = 0,

    // Movement
    MoveForward,
    MoveBackward,
    MoveLeft,
    MoveRight,
    Jump,
    Crouch,
    Sprint,

    // Mouse/Aim
    MouseMove,      // Relative mouse movement
    MouseMoveTo,    // Move toward a target position

    // Combat
    Attack,         // Primary fire (mouse1)
    AttackSecondary,// Secondary fire (mouse2)
    Reload,

    // Interaction
    Use,            // E key - interact/repair

    // Weapon selection
    Weapon1,
    Weapon2,
    Weapon3,
    Weapon4,
    WeaponNext,
    WeaponPrev,

    // Special
    Flashlight,
    Voice,          // Voice chat
    Chat,

    // Composite (executed by skills)
    StopAll,        // Release all keys
}

/// <summary>
/// High-level strategic modes (Tier 1 decisions).
/// </summary>
public enum StrategicMode
{
    /// <summary>No specific mode - idle.</summary>
    Idle,

    /// <summary>Actively engaging threats.</summary>
    Fight,

    /// <summary>Retreating while fighting.</summary>
    Kite,

    /// <summary>Reloading weapon.</summary>
    Reload,

    /// <summary>Seeking health/healing.</summary>
    Heal,

    /// <summary>Repairing barricades.</summary>
    Repair,

    /// <summary>Moving to a better position.</summary>
    Reposition,

    /// <summary>Purchasing items/weapons.</summary>
    Buy,

    /// <summary>Following/supporting teammates.</summary>
    Support,

    /// <summary>Recovering from stuck state.</summary>
    Unstick,

    /// <summary>Seeking resources/objectives.</summary>
    Seek,
}
