using OKET.Core.Types;

namespace OKET.Vision.Hud;

/// <summary>
/// Defines the screen regions where HUD elements appear.
/// Configured based on screen resolution.
/// </summary>
public sealed class HudRegions
{
    public int ScreenWidth { get; private set; }
    public int ScreenHeight { get; private set; }

    // Health bar region (typically bottom-left)
    public BoundingBox HealthRegion { get; private set; }

    // Armor region (near health)
    public BoundingBox ArmorRegion { get; private set; }

    // Ammo display region (typically bottom-right)
    public BoundingBox AmmoClipRegion { get; private set; }
    public BoundingBox AmmoReserveRegion { get; private set; }

    // Wave/round info (typically top-center or top-right)
    public BoundingBox WaveRegion { get; private set; }

    // Points display
    public BoundingBox PointsRegion { get; private set; }

    // Timer region
    public BoundingBox TimerRegion { get; private set; }

    // Crosshair region (center screen)
    public BoundingBox CrosshairRegion { get; private set; }

    // Death screen region
    public BoundingBox DeathOverlayRegion { get; private set; }

    public HudRegions()
    {
        Configure(1920, 1080);
    }

    /// <summary>
    /// Configure HUD regions for the given screen resolution.
    /// These values are calibrated for Zombie Survival's default HUD.
    /// </summary>
    public void Configure(int width, int height)
    {
        ScreenWidth = width;
        ScreenHeight = height;

        float scaleX = width / 1920f;
        float scaleY = height / 1080f;

        // Health - bottom left corner
        HealthRegion = new BoundingBox(
            20 * scaleX,
            height - 80 * scaleY,
            200 * scaleX,
            60 * scaleY);

        // Armor - near health
        ArmorRegion = new BoundingBox(
            20 * scaleX,
            height - 120 * scaleY,
            200 * scaleX,
            40 * scaleY);

        // Ammo clip - bottom right
        AmmoClipRegion = new BoundingBox(
            width - 200 * scaleX,
            height - 80 * scaleY,
            100 * scaleX,
            60 * scaleY);

        // Ammo reserve - next to clip
        AmmoReserveRegion = new BoundingBox(
            width - 100 * scaleX,
            height - 80 * scaleY,
            80 * scaleX,
            60 * scaleY);

        // Wave info - top center
        WaveRegion = new BoundingBox(
            width / 2f - 100 * scaleX,
            20 * scaleY,
            200 * scaleX,
            50 * scaleY);

        // Points - top right or near health
        PointsRegion = new BoundingBox(
            width - 250 * scaleX,
            20 * scaleY,
            200 * scaleX,
            40 * scaleY);

        // Timer - top center
        TimerRegion = new BoundingBox(
            width / 2f - 60 * scaleX,
            70 * scaleY,
            120 * scaleX,
            40 * scaleY);

        // Crosshair - center of screen
        CrosshairRegion = new BoundingBox(
            width / 2f - 50,
            height / 2f - 50,
            100,
            100);

        // Death overlay - large center area
        DeathOverlayRegion = new BoundingBox(
            width / 4f,
            height / 4f,
            width / 2f,
            height / 2f);
    }
}
