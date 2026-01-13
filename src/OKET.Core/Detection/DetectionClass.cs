namespace OKET.Core.Detection;

/// <summary>
/// Classes of objects the vision system can detect.
/// </summary>
public enum DetectionClass
{
    Unknown = 0,

    // Enemies
    Zombie = 1,
    ZombieHead = 2,
    FastZombie = 3,
    PoisonZombie = 4,
    Headcrab = 5,

    // World objects
    Barricade = 10,
    BarricadeBoard = 11,
    Door = 12,
    AmmoCrate = 13,
    WeaponCrate = 14,
    HealthKit = 15,

    // Players
    Player = 20,
    PlayerHead = 21,
    Teammate = 22,
    Survivor = 23,
    SurvivorHead = 24,

    // UI elements
    Crosshair = 30,
    HitMarker = 31,
    DamageIndicator = 32,

    // Props
    Prop = 40,
    ExplosiveBarrel = 41
}
