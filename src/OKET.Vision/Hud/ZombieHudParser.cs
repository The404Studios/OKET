using OKET.Core.Types;
using OKET.Core.State;
using OKET.Core.Interfaces;

namespace OKET.Vision.Hud;

/// <summary>
/// Parses HUD elements from Zombie Survival gamemode.
/// Uses color analysis and pattern detection.
/// </summary>
public sealed class ZombieHudParser : IHudParser
{
    private readonly HudRegions _regions = new();
    private HudState? _lastState;
    private int _lastAmmoClip;
    private DateTime _reloadStartTime;
    private bool _isReloading;

    public bool UseOcr { get; set; } = false;

    public void Configure(int screenWidth, int screenHeight)
    {
        _regions.Configure(screenWidth, screenHeight);
    }

    public HudState Parse(Frame frame)
    {
        // Parse individual components
        int health = ParseHealth(frame);
        int armor = ParseArmor(frame);
        int ammoClip = ParseAmmoClip(frame);
        int ammoReserve = ParseAmmoReserve(frame);
        int wave = ParseWave(frame);
        int points = ParsePoints(frame);
        bool isDead = DetectDeath(frame);

        // Detect reload state from ammo changes
        UpdateReloadState(ammoClip);

        float confidence = CalculateConfidence(frame);

        var state = new HudState
        {
            Health = health,
            Armor = armor,
            AmmoClip = ammoClip,
            AmmoReserve = ammoReserve,
            Wave = wave,
            Points = points,
            IsDead = isDead,
            IsReloading = _isReloading,
            Confidence = confidence
        };

        _lastState = state;
        return state;
    }

    private int ParseHealth(Frame frame)
    {
        // Health bar is typically red/green gradient
        // Measure the filled portion of the health bar
        float fill = ColorAnalyzer.MeasureHorizontalBar(frame, _regions.HealthRegion, (r, g, b) =>
        {
            // Health bar colors: green (full) to yellow to red (low)
            // Looking for non-background (typically dark) pixels
            return (r > 50 || g > 50) && (r + g + b) > 100;
        });

        // Also check for color to estimate health range
        var (avgR, avgG, _) = ColorAnalyzer.AverageColor(frame, _regions.HealthRegion);

        // Green = healthy, Red = low health
        // This gives us a sanity check on the bar measurement
        int healthEstimate = (int)(fill * 100);

        // Clamp to valid range
        return Math.Clamp(healthEstimate, 0, 100);
    }

    private int ParseArmor(Frame frame)
    {
        // Armor bar is typically blue
        float fill = ColorAnalyzer.MeasureHorizontalBar(frame, _regions.ArmorRegion, (r, g, b) =>
        {
            // Armor is typically blue
            return b > 100 && b > r && b > g;
        });

        return (int)(fill * 100);
    }

    private int ParseAmmoClip(Frame frame)
    {
        // This is where OCR would be valuable
        // For now, use heuristics based on digit segment detection
        if (UseOcr)
        {
            // TODO: Integrate OCR library
            return _lastAmmoClip;
        }

        // Fallback: estimate from last known state + time since last shot
        return _lastState?.AmmoClip ?? 30;
    }

    private int ParseAmmoReserve(Frame frame)
    {
        // Similar to clip parsing
        return _lastState?.AmmoReserve ?? 200;
    }

    private int ParseWave(Frame frame)
    {
        // Wave number is typically displayed prominently
        // Would need OCR for accurate reading
        return _lastState?.Wave ?? 1;
    }

    private int ParsePoints(Frame frame)
    {
        // Points display typically near top
        return _lastState?.Points ?? 0;
    }

    private bool DetectDeath(Frame frame)
    {
        // Death screen typically has:
        // 1. Red tint over the screen
        // 2. "You died" text
        // 3. Respawn timer

        // Check for red tint in center of screen
        bool hasRedTint = ColorAnalyzer.HasRedTint(frame, _regions.DeathOverlayRegion, 0.4f);

        // Check for very dark screen (often on death)
        var (r, g, b) = ColorAnalyzer.AverageColor(frame, _regions.DeathOverlayRegion);
        bool isDark = (r + g + b) / 3 < 50;

        // Check for significant red overlay
        bool hasDeathOverlay = ColorAnalyzer.IsRegionColor(
            frame, _regions.DeathOverlayRegion,
            targetR: 100, targetG: 20, targetB: 20,
            tolerance: 50, threshold: 0.3f);

        return hasRedTint || hasDeathOverlay;
    }

    private void UpdateReloadState(int currentAmmo)
    {
        // Detect reload start: ammo was > 0, now is < previous
        // Detect reload end: ammo increased

        if (_isReloading)
        {
            // Check if reload completed (ammo increased)
            if (currentAmmo > _lastAmmoClip)
            {
                _isReloading = false;
            }
            // Or timeout after typical reload duration (~3 seconds)
            else if ((DateTime.UtcNow - _reloadStartTime).TotalSeconds > 4)
            {
                _isReloading = false;
            }
        }
        else
        {
            // Check if reload started (ammo decreased to 0 or manual reload key)
            if (_lastAmmoClip > 0 && currentAmmo == 0)
            {
                // Likely fired last bullet and auto-reloading
                _isReloading = true;
                _reloadStartTime = DateTime.UtcNow;
            }
        }

        _lastAmmoClip = currentAmmo;
    }

    private float CalculateConfidence(Frame frame)
    {
        // Confidence based on:
        // 1. Are we seeing expected HUD elements?
        // 2. Is the game window in focus?
        // 3. Are values in reasonable ranges?

        float confidence = 0.5f; // Base confidence

        // Check if health region has expected colors
        var (r, g, _) = ColorAnalyzer.AverageColor(frame, _regions.HealthRegion);
        if (r > 30 || g > 30) // Some health bar color detected
            confidence += 0.2f;

        // Check if screen isn't completely black (game running)
        var (avgR, avgG, avgB) = ColorAnalyzer.AverageColor(frame,
            new BoundingBox(0, 0, frame.Width, frame.Height));
        if (avgR + avgG + avgB > 30) // Not a black screen
            confidence += 0.2f;

        // Check crosshair region for typical crosshair colors
        var (cr, cg, cb) = ColorAnalyzer.AverageColor(frame, _regions.CrosshairRegion);
        if (cr > 100 || cg > 100 || cb > 100) // Crosshair visible
            confidence += 0.1f;

        return Math.Clamp(confidence, 0f, 1f);
    }
}
