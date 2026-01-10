namespace OKET.Core.State;

/// <summary>
/// State extracted from HUD elements.
/// </summary>
public sealed class HudState
{
    /// <summary>Current health [0, 100+].</summary>
    public int Health { get; init; }

    /// <summary>Current armor [0, 100+].</summary>
    public int Armor { get; init; }

    /// <summary>Ammo in current magazine.</summary>
    public int AmmoClip { get; init; }

    /// <summary>Total reserve ammo.</summary>
    public int AmmoReserve { get; init; }

    /// <summary>Current wave number.</summary>
    public int Wave { get; init; }

    /// <summary>Points/currency available.</summary>
    public int Points { get; init; }

    /// <summary>Time remaining in wave (seconds), if visible.</summary>
    public int? TimeRemaining { get; init; }

    /// <summary>Whether the player appears to be dead.</summary>
    public bool IsDead { get; init; }

    /// <summary>Whether a reload is in progress (estimated from ammo changes).</summary>
    public bool IsReloading { get; init; }

    /// <summary>Confidence in the HUD reading [0, 1].</summary>
    public float Confidence { get; init; }

    public bool IsLowHealth => Health <= 30;
    public bool IsCriticalHealth => Health <= 15;
    public bool NeedsReload => AmmoClip == 0 && AmmoReserve > 0;
    public bool IsLowAmmo => AmmoClip <= 5 && AmmoReserve > 0;
    public bool HasNoAmmo => AmmoClip == 0 && AmmoReserve == 0;
}
