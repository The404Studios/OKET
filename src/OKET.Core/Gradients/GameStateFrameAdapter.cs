using OKET.Core.Detection;
using OKET.Core.State;

namespace OKET.Core.Gradients;

/// <summary>
/// Adapts GameState and DetectionResult to FrameData for gradient processing.
///
/// This bridges the existing detection system with the gradient system,
/// allowing them to work in parallel and learn from each other.
///
/// Conversion strategy:
/// - Detections → High-intensity, high-edge, colored regions
/// - HUD data → Static UI fields at screen edges
/// - Aim state → Flow vectors toward aim point
/// - Audio cues → Temporal change spikes
/// </summary>
public sealed class GameStateFrameAdapter : FrameData
{
    private readonly int _width;
    private readonly int _height;

    // Simulated field data derived from game state
    private readonly float[,] _intensity;
    private readonly float[,] _edges;
    private readonly float[,] _hue;
    private readonly float[,] _saturation;
    private readonly float[,] _value;
    private readonly float[,] _flowX;
    private readonly float[,] _flowY;

    // Tracking
    private long _lastFrame;
    private readonly float[,] _prevIntensity;

    public GameStateFrameAdapter(int width = 1920, int height = 1080)
    {
        _width = width;
        _height = height;

        _intensity = new float[width, height];
        _edges = new float[width, height];
        _hue = new float[width, height];
        _saturation = new float[width, height];
        _value = new float[width, height];
        _flowX = new float[width, height];
        _flowY = new float[width, height];
        _prevIntensity = new float[width, height];
    }

    /// <summary>
    /// Update from game state.
    /// </summary>
    public void Update(GameState gameState, Core.Audio.AudioSnapshot audioSnapshot)
    {
        // Store previous for flow calculation
        Array.Copy(_intensity, _prevIntensity, _intensity.Length);

        // Clear fields
        Clear();

        // Project detections onto field
        ProjectDetections(gameState.Detections);

        // Add HUD regions
        ProjectHUD(gameState.Hud);

        // Add aim influence
        ProjectAim(gameState.Aim);

        // Add audio as temporal spikes
        ProjectAudio(audioSnapshot);

        // Compute flow from intensity change
        ComputeFlow();

        _lastFrame = gameState.FrameId;
    }

    private void Clear()
    {
        for (int y = 0; y < _height; y++)
        {
            for (int x = 0; x < _width; x++)
            {
                _intensity[x, y] = 0.1f; // Base intensity
                _edges[x, y] = 0;
                _hue[x, y] = 0;
                _saturation[x, y] = 0;
                _value[x, y] = 0.1f;
                _flowX[x, y] = 0;
                _flowY[x, y] = 0;
            }
        }
    }

    private void ProjectDetections(DetectionResult detections)
    {
        foreach (var detection in detections.Detections)
        {
            // Get bounding box in screen coordinates
            var box = detection.Box;
            int left = Math.Clamp((int)box.Left, 0, _width - 1);
            int top = Math.Clamp((int)box.Top, 0, _height - 1);
            int right = Math.Clamp((int)box.Right, 0, _width - 1);
            int bottom = Math.Clamp((int)box.Bottom, 0, _height - 1);

            // Get color based on detection class
            var (h, s, v) = GetDetectionColor(detection.Class);

            // Fill detection region
            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    _intensity[x, y] = Math.Max(_intensity[x, y], detection.Confidence);
                    _hue[x, y] = h;
                    _saturation[x, y] = s;
                    _value[x, y] = v * detection.Confidence;

                    // Edges at border
                    if (x == left || x == right || y == top || y == bottom)
                    {
                        _edges[x, y] = detection.Confidence;
                    }
                }
            }

            // Add velocity as flow
            if (detection.Velocity != null)
            {
                float vx = detection.Velocity.Value.X / 100f; // Normalize
                float vy = detection.Velocity.Value.Y / 100f;

                for (int y = top; y <= bottom; y++)
                {
                    for (int x = left; x <= right; x++)
                    {
                        _flowX[x, y] = vx;
                        _flowY[x, y] = vy;
                    }
                }
            }
        }
    }

    private static (float h, float s, float v) GetDetectionColor(DetectionClass cls)
    {
        return cls switch
        {
            // Threats - red/orange
            DetectionClass.Zombie => (0.0f, 0.8f, 0.9f),
            DetectionClass.FastZombie => (0.05f, 0.9f, 1.0f),
            DetectionClass.PoisonZombie => (0.3f, 0.7f, 0.8f), // Green-ish
            DetectionClass.Headcrab => (0.1f, 0.7f, 0.7f),
            DetectionClass.ZombieHead => (0.02f, 0.8f, 0.85f),

            // Items - various colors
            DetectionClass.AmmoCrate => (0.15f, 0.6f, 0.8f), // Yellow
            DetectionClass.WeaponCrate => (0.55f, 0.7f, 0.9f), // Cyan
            DetectionClass.HealthKit => (0.35f, 0.8f, 0.9f), // Green

            // Structure
            DetectionClass.Barricade => (0.08f, 0.3f, 0.6f), // Brown
            DetectionClass.BarricadeBoard => (0.08f, 0.2f, 0.5f),
            DetectionClass.Door => (0.6f, 0.2f, 0.7f), // Blue-gray

            // Default
            _ => (0f, 0f, 0.5f)
        };
    }

    private void ProjectHUD(HudState hud)
    {
        // Health bar (bottom left)
        int healthWidth = (int)(200 * (hud.Health / 100f));
        for (int y = _height - 50; y < _height - 30; y++)
        {
            for (int x = 20; x < 20 + healthWidth; x++)
            {
                _intensity[x, y] = 0.9f;
                _hue[x, y] = hud.Health > 50 ? 0.35f : 0.0f; // Green or red
                _saturation[x, y] = 0.8f;
                _value[x, y] = 0.9f;
            }
        }

        // Ammo (bottom right)
        int ammoWidth = (int)(150 * Math.Min(1f, hud.AmmoClip / 30f));
        for (int y = _height - 50; y < _height - 30; y++)
        {
            for (int x = _width - 170; x < _width - 170 + ammoWidth; x++)
            {
                _intensity[x, y] = 0.8f;
                _hue[x, y] = 0.15f; // Yellow
                _saturation[x, y] = 0.7f;
                _value[x, y] = 0.85f;
            }
        }
    }

    private void ProjectAim(AimState aim)
    {
        // Create flow toward aim point
        int aimX = _width / 2 + (int)(aim.OffsetToTarget.X / 10f);
        int aimY = _height / 2 + (int)(aim.OffsetToTarget.Y / 10f);

        // Radial flow toward center in a region around aim
        int radius = 100;
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int x = aimX + dx;
                int y = aimY + dy;

                if (x < 0 || x >= _width || y < 0 || y >= _height)
                    continue;

                float dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist > radius) continue;

                float strength = (1f - dist / radius) * 0.3f;
                _flowX[x, y] += -dx / (dist + 1) * strength;
                _flowY[x, y] += -dy / (dist + 1) * strength;
            }
        }

        // Mark crosshair area
        for (int dy = -5; dy <= 5; dy++)
        {
            for (int dx = -5; dx <= 5; dx++)
            {
                int x = aimX + dx;
                int y = aimY + dy;
                if (x >= 0 && x < _width && y >= 0 && y < _height)
                {
                    _edges[x, y] = 0.5f;
                    _intensity[x, y] = 0.7f;
                }
            }
        }
    }

    private void ProjectAudio(Core.Audio.AudioSnapshot audio)
    {
        // Audio creates temporal intensity spikes in relevant regions
        if (audio.HasThreatSounds)
        {
            // Spike in center-lower region (where threats typically appear)
            for (int y = _height / 2; y < _height - 100; y++)
            {
                for (int x = _width / 4; x < 3 * _width / 4; x++)
                {
                    _intensity[x, y] = Math.Max(_intensity[x, y], 0.3f);
                }
            }
        }

        if (audio.HasDamageSounds)
        {
            // Spike everywhere (damage affects whole perception)
            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    _intensity[x, y] = Math.Min(1f, _intensity[x, y] + 0.1f);
                    _hue[x, y] = 0f; // Shift toward red
                }
            }
        }
    }

    private void ComputeFlow()
    {
        // Simple flow from intensity gradient
        for (int y = 1; y < _height - 1; y++)
        {
            for (int x = 1; x < _width - 1; x++)
            {
                float dx = _intensity[x + 1, y] - _intensity[x - 1, y];
                float dy = _intensity[x, y + 1] - _intensity[x, y - 1];

                // Temporal component
                float dt = _intensity[x, y] - _prevIntensity[x, y];

                _flowX[x, y] += dx * 0.5f + dt * 0.1f;
                _flowY[x, y] += dy * 0.5f;
            }
        }
    }

    // FrameData interface implementation
    public float GetIntensity(int x, int y)
    {
        if (x < 0 || x >= _width || y < 0 || y >= _height)
            return 0;
        return _intensity[x, y];
    }

    public float GetEdge(int x, int y)
    {
        if (x < 0 || x >= _width || y < 0 || y >= _height)
            return 0;
        return _edges[x, y];
    }

    public (float h, float s, float v) GetHSV(int x, int y)
    {
        if (x < 0 || x >= _width || y < 0 || y >= _height)
            return (0, 0, 0);
        return (_hue[x, y], _saturation[x, y], _value[x, y]);
    }

    public (float fx, float fy) GetFlow(int x, int y)
    {
        if (x < 0 || x >= _width || y < 0 || y >= _height)
            return (0, 0);
        return (_flowX[x, y], _flowY[x, y]);
    }
}
