using System.Drawing;
using System.Drawing.Drawing2D;
using OKET.Core.Types;

namespace OKET.Vision.Overlay;

/// <summary>
/// Smooth rendering layer for overlay visualizations.
/// Handles interpolation, double-buffering, and smooth transitions.
/// This is a SEPARATE layer from the agent logic - it only draws.
/// </summary>
public sealed class SmoothRenderer : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly object _lock = new();

    // Double buffering
    private Bitmap _frontBuffer;
    private Bitmap _backBuffer;
    private Graphics _backGraphics;

    // Smooth detection tracking
    private readonly Dictionary<int, SmoothDetection> _smoothDetections = new();
    private readonly List<FadingDetection> _fadingDetections = new();

    // Timing
    private DateTime _lastRenderTime = DateTime.UtcNow;
    private float _deltaTime;

    // Rendering state
    private DebugState _currentDebugState = new();
    private readonly List<SmoothMarker> _markers = new();
    private readonly List<SmoothPath> _paths = new();

    // Configuration
    public float InterpolationSpeed { get; set; } = 15f; // Higher = faster snap
    public float FadeSpeed { get; set; } = 3f; // Seconds to fade out
    public bool EnableSmoothing { get; set; } = true;

    public SmoothRenderer(int width, int height)
    {
        _width = width;
        _height = height;

        // Create double buffers
        _frontBuffer = new Bitmap(width, height);
        _backBuffer = new Bitmap(width, height);
        _backGraphics = Graphics.FromImage(_backBuffer);
        _backGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        _backGraphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    /// <summary>
    /// Update detection data. Detections will be smoothly interpolated.
    /// </summary>
    public void UpdateDetections(IEnumerable<DetectionData> detections)
    {
        lock (_lock)
        {
            var currentIds = new HashSet<int>();

            foreach (var det in detections)
            {
                currentIds.Add(det.TrackId);

                if (_smoothDetections.TryGetValue(det.TrackId, out var existing))
                {
                    // Update existing - smooth interpolation will happen in render
                    existing.TargetBox = det.Box;
                    existing.TargetConfidence = det.Confidence;
                    existing.ClassName = det.ClassName;
                    existing.IsThreat = det.IsThreat;
                    existing.Velocity = det.Velocity;
                    existing.LastSeen = DateTime.UtcNow;
                }
                else
                {
                    // New detection - start at target position
                    _smoothDetections[det.TrackId] = new SmoothDetection
                    {
                        TrackId = det.TrackId,
                        CurrentBox = det.Box,
                        TargetBox = det.Box,
                        CurrentConfidence = det.Confidence,
                        TargetConfidence = det.Confidence,
                        ClassName = det.ClassName,
                        IsThreat = det.IsThreat,
                        Velocity = det.Velocity,
                        LastSeen = DateTime.UtcNow,
                        Alpha = 0f, // Start invisible, fade in
                        TargetAlpha = 1f
                    };
                }
            }

            // Move disappeared detections to fading list
            var disappeared = _smoothDetections.Keys.Except(currentIds).ToList();
            foreach (var id in disappeared)
            {
                var det = _smoothDetections[id];
                _fadingDetections.Add(new FadingDetection
                {
                    Box = det.CurrentBox,
                    ClassName = det.ClassName,
                    Alpha = det.Alpha,
                    Color = det.IsThreat ? Color.Red : Color.Gray
                });
                _smoothDetections.Remove(id);
            }
        }
    }

    /// <summary>
    /// Update debug state for panel rendering.
    /// </summary>
    public void UpdateDebugState(DebugState state)
    {
        lock (_lock)
        {
            _currentDebugState = state;
        }
    }

    /// <summary>
    /// Add a marker at a position with smooth fade.
    /// </summary>
    public void AddMarker(Vector2 position, string label, Color color, float duration = 1f)
    {
        lock (_lock)
        {
            _markers.Add(new SmoothMarker
            {
                Position = position,
                Label = label,
                Color = color,
                Alpha = 1f,
                Duration = duration,
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Set a path to render.
    /// </summary>
    public void SetPath(List<Vector2> waypoints, Color color, int currentIndex = 0)
    {
        lock (_lock)
        {
            _paths.Clear();
            if (waypoints.Count >= 2)
            {
                _paths.Add(new SmoothPath
                {
                    Waypoints = waypoints.ToList(),
                    Color = color,
                    CurrentIndex = currentIndex,
                    Alpha = 1f
                });
            }
        }
    }

    /// <summary>
    /// Render the current state to a bitmap. Call this from the render thread.
    /// </summary>
    public Bitmap Render()
    {
        // Calculate delta time
        var now = DateTime.UtcNow;
        _deltaTime = (float)(now - _lastRenderTime).TotalSeconds;
        _lastRenderTime = now;

        // Clamp delta to avoid huge jumps
        _deltaTime = Math.Min(_deltaTime, 0.1f);

        lock (_lock)
        {
            // Clear back buffer
            _backGraphics.Clear(Color.Transparent);

            // Update and render all elements
            UpdateAndRenderPaths(_backGraphics);
            UpdateAndRenderDetections(_backGraphics);
            UpdateAndRenderMarkers(_backGraphics);
            RenderDebugPanel(_backGraphics);

            // Swap buffers
            var temp = _frontBuffer;
            _frontBuffer = _backBuffer;
            _backBuffer = temp;
            _backGraphics.Dispose();
            _backGraphics = Graphics.FromImage(_backBuffer);
            _backGraphics.SmoothingMode = SmoothingMode.AntiAlias;
            _backGraphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        }

        // Return a copy of the front buffer
        return (Bitmap)_frontBuffer.Clone();
    }

    private void UpdateAndRenderDetections(Graphics g)
    {
        // Update and render active detections
        foreach (var det in _smoothDetections.Values)
        {
            // Interpolate position
            if (EnableSmoothing)
            {
                float t = Math.Min(1f, _deltaTime * InterpolationSpeed);
                det.CurrentBox = LerpBox(det.CurrentBox, det.TargetBox, t);
                det.CurrentConfidence = Lerp(det.CurrentConfidence, det.TargetConfidence, t);
                det.Alpha = Lerp(det.Alpha, det.TargetAlpha, t * 2); // Faster fade in
            }
            else
            {
                det.CurrentBox = det.TargetBox;
                det.CurrentConfidence = det.TargetConfidence;
                det.Alpha = det.TargetAlpha;
            }

            RenderDetection(g, det);
        }

        // Update and render fading detections
        for (int i = _fadingDetections.Count - 1; i >= 0; i--)
        {
            var fading = _fadingDetections[i];
            fading.Alpha -= _deltaTime * FadeSpeed;

            if (fading.Alpha <= 0)
            {
                _fadingDetections.RemoveAt(i);
                continue;
            }

            RenderFadingDetection(g, fading);
        }
    }

    private void RenderDetection(Graphics g, SmoothDetection det)
    {
        var box = det.CurrentBox;
        int alpha = (int)(det.Alpha * 255);
        if (alpha <= 0) return;

        // Choose color based on type
        Color baseColor = det.IsThreat
            ? Color.FromArgb(alpha, 255, 60, 60)    // Red for threats
            : Color.FromArgb(alpha, 60, 200, 255);  // Cyan for others

        // Draw bounding box with smooth alpha
        using var pen = new Pen(baseColor, 2f);
        g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);

        // Draw corner accents
        float cornerSize = Math.Min(box.Width, box.Height) * 0.2f;
        using var accentPen = new Pen(baseColor, 3f);

        // Top-left
        g.DrawLine(accentPen, box.X, box.Y, box.X + cornerSize, box.Y);
        g.DrawLine(accentPen, box.X, box.Y, box.X, box.Y + cornerSize);

        // Top-right
        g.DrawLine(accentPen, box.X + box.Width, box.Y, box.X + box.Width - cornerSize, box.Y);
        g.DrawLine(accentPen, box.X + box.Width, box.Y, box.X + box.Width, box.Y + cornerSize);

        // Bottom-left
        g.DrawLine(accentPen, box.X, box.Y + box.Height, box.X + cornerSize, box.Y + box.Height);
        g.DrawLine(accentPen, box.X, box.Y + box.Height, box.X, box.Y + box.Height - cornerSize);

        // Bottom-right
        g.DrawLine(accentPen, box.X + box.Width, box.Y + box.Height, box.X + box.Width - cornerSize, box.Y + box.Height);
        g.DrawLine(accentPen, box.X + box.Width, box.Y + box.Height, box.X + box.Width, box.Y + box.Height - cornerSize);

        // Draw label
        string label = $"[{det.TrackId}] {det.ClassName} ({det.CurrentConfidence:P0})";
        using var font = new Font("Consolas", 9, FontStyle.Bold);
        var labelSize = g.MeasureString(label, font);

        float labelX = box.X;
        float labelY = box.Y - labelSize.Height - 2;
        if (labelY < 0) labelY = box.Y + box.Height + 2;

        // Label background
        using var bgBrush = new SolidBrush(Color.FromArgb((int)(alpha * 0.7f), 0, 0, 0));
        g.FillRectangle(bgBrush, labelX, labelY, labelSize.Width + 4, labelSize.Height);

        // Label text
        using var textBrush = new SolidBrush(baseColor);
        g.DrawString(label, font, textBrush, labelX + 2, labelY);

        // Draw velocity arrow if moving
        if (det.Velocity.X != 0 || det.Velocity.Y != 0)
        {
            float cx = box.X + box.Width / 2;
            float cy = box.Y + box.Height / 2;
            float scale = 15f;

            using var velocityPen = new Pen(Color.FromArgb(alpha, 255, 255, 255), 2f);
            velocityPen.EndCap = LineCap.ArrowAnchor;
            g.DrawLine(velocityPen, cx, cy, cx + det.Velocity.X * scale, cy + det.Velocity.Y * scale);
        }

        // Threat indicator
        if (det.IsThreat)
        {
            using var threatBrush = new SolidBrush(Color.FromArgb((int)(alpha * 0.6f), 255, 0, 0));
            g.FillEllipse(threatBrush, box.X - 10, box.Y, 8, 8);
        }
    }

    private void RenderFadingDetection(Graphics g, FadingDetection det)
    {
        int alpha = (int)(det.Alpha * 180);
        if (alpha <= 0) return;

        var color = Color.FromArgb(alpha, det.Color.R, det.Color.G, det.Color.B);
        using var pen = new Pen(color, 1f);
        pen.DashStyle = DashStyle.Dash;
        g.DrawRectangle(pen, det.Box.X, det.Box.Y, det.Box.Width, det.Box.Height);
    }

    private void UpdateAndRenderMarkers(Graphics g)
    {
        var now = DateTime.UtcNow;

        for (int i = _markers.Count - 1; i >= 0; i--)
        {
            var marker = _markers[i];
            float elapsed = (float)(now - marker.CreatedAt).TotalSeconds;

            if (elapsed >= marker.Duration)
            {
                _markers.RemoveAt(i);
                continue;
            }

            // Fade out near end
            float alpha = 1f;
            if (elapsed > marker.Duration * 0.7f)
            {
                alpha = 1f - ((elapsed - marker.Duration * 0.7f) / (marker.Duration * 0.3f));
            }

            int a = (int)(alpha * 255);
            var color = Color.FromArgb(a, marker.Color.R, marker.Color.G, marker.Color.B);

            using var brush = new SolidBrush(color);
            using var pen = new Pen(color, 2f);

            // Draw crosshair marker
            float size = 12f;
            g.DrawLine(pen, marker.Position.X - size, marker.Position.Y, marker.Position.X + size, marker.Position.Y);
            g.DrawLine(pen, marker.Position.X, marker.Position.Y - size, marker.Position.X, marker.Position.Y + size);
            g.DrawEllipse(pen, marker.Position.X - size / 2, marker.Position.Y - size / 2, size, size);

            // Label
            if (!string.IsNullOrEmpty(marker.Label))
            {
                using var font = new Font("Consolas", 8, FontStyle.Bold);
                g.DrawString(marker.Label, font, brush, marker.Position.X + size, marker.Position.Y - size);
            }
        }
    }

    private void UpdateAndRenderPaths(Graphics g)
    {
        foreach (var path in _paths)
        {
            if (path.Waypoints.Count < 2) continue;

            int alpha = (int)(path.Alpha * 200);
            using var pen = new Pen(Color.FromArgb(alpha, path.Color.R, path.Color.G, path.Color.B), 3f);
            pen.EndCap = LineCap.ArrowAnchor;

            for (int i = 0; i < path.Waypoints.Count - 1; i++)
            {
                var from = path.Waypoints[i];
                var to = path.Waypoints[i + 1];

                // Dim traversed segments
                if (i < path.CurrentIndex)
                {
                    using var dimPen = new Pen(Color.FromArgb(alpha / 3, path.Color.R, path.Color.G, path.Color.B), 2f);
                    g.DrawLine(dimPen, from.X, from.Y, to.X, to.Y);
                }
                else
                {
                    g.DrawLine(pen, from.X, from.Y, to.X, to.Y);
                }
            }

            // Waypoint circles
            using var waypointBrush = new SolidBrush(Color.FromArgb(alpha, 255, 255, 0));
            foreach (var wp in path.Waypoints)
            {
                g.FillEllipse(waypointBrush, wp.X - 4, wp.Y - 4, 8, 8);
            }
        }
    }

    private void RenderDebugPanel(Graphics g)
    {
        const int panelX = 10;
        const int panelY = 10;
        const int panelWidth = 300;
        const int lineHeight = 18;
        const int padding = 10;

        var state = _currentDebugState;

        var lines = new List<(string text, Color color)>
        {
            ("═══ OKET AGI v0.2 ═══", Color.Cyan),
            ("", Color.Transparent),
            ($"Intent: {state.IntentType}", GetIntentColor(state.IntentType)),
            ($"  └─ {state.IntentReason}", Color.FromArgb(200, 180, 180, 180)),
            ($"Confidence: {state.Confidence:P0}", GetConfidenceColor(state.Confidence)),
            ("", Color.Transparent),
            ($"Skill: {state.ActiveSkill}", Color.White),
            ($"Action: {state.ChosenAction}", Color.Yellow),
            ("", Color.Transparent),
            ($"Pred Error: {state.PredictionError:F1}px", GetErrorColor(state.PredictionError)),
            ($"Reward: {state.LastReward:+0.00;-0.00;0}", GetRewardColor(state.LastReward)),
            ("", Color.Transparent),
            ($"Threats: {state.ThreatCount}", state.ThreatCount > 0 ? Color.Red : Color.LimeGreen),
            ($"Health: {state.Health}%", GetHealthColor(state.Health)),
            ($"FPS: {state.Fps:F0}", Color.White)
        };

        // Filter empty lines for height calculation
        int visibleLines = lines.Count(l => !string.IsNullOrEmpty(l.text));
        int panelHeight = (visibleLines * lineHeight) + (padding * 2);

        // Panel background with transparency
        using var bgBrush = new SolidBrush(Color.FromArgb(220, 15, 15, 20));
        using var borderPen = new Pen(Color.FromArgb(200, 60, 60, 80), 2);
        g.FillRectangle(bgBrush, panelX, panelY, panelWidth, panelHeight);
        g.DrawRectangle(borderPen, panelX, panelY, panelWidth, panelHeight);

        // Render lines
        using var font = new Font("Consolas", 10, FontStyle.Regular);
        int y = panelY + padding;

        foreach (var (text, color) in lines)
        {
            if (string.IsNullOrEmpty(text)) continue;

            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, panelX + padding, y);
            y += lineHeight;
        }

        // Confidence bar
        int barX = panelX + padding;
        int barY = panelY + panelHeight + 5;
        int barWidth = panelWidth - (padding * 2);
        int barHeight = 6;

        using var barBgBrush = new SolidBrush(Color.FromArgb(100, 40, 40, 40));
        g.FillRectangle(barBgBrush, barX, barY, barWidth, barHeight);

        int fillWidth = (int)(barWidth * Math.Clamp(state.Confidence, 0f, 1f));
        using var barFillBrush = new SolidBrush(GetConfidenceColor(state.Confidence));
        g.FillRectangle(barFillBrush, barX, barY, fillWidth, barHeight);
    }

    // Helper methods
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static BoundingBox LerpBox(BoundingBox a, BoundingBox b, float t)
    {
        return new BoundingBox(
            Lerp(a.X, b.X, t),
            Lerp(a.Y, b.Y, t),
            Lerp(a.Width, b.Width, t),
            Lerp(a.Height, b.Height, t)
        );
    }

    private static Color GetIntentColor(string intent) => intent.ToLower() switch
    {
        "idle" => Color.Gray,
        "survive" => Color.Yellow,
        "engageenemy" => Color.Red,
        "reachtarget" => Color.Cyan,
        "acquireitem" => Color.LimeGreen,
        "avoidthreat" => Color.Orange,
        "explore" => Color.LightBlue,
        _ => Color.White
    };

    private static Color GetConfidenceColor(float conf) => conf switch
    {
        >= 0.8f => Color.LimeGreen,
        >= 0.5f => Color.Yellow,
        >= 0.3f => Color.Orange,
        _ => Color.Red
    };

    private static Color GetErrorColor(float error) => error switch
    {
        < 10f => Color.LimeGreen,
        < 30f => Color.Yellow,
        < 50f => Color.Orange,
        _ => Color.Red
    };

    private static Color GetRewardColor(float reward) => reward switch
    {
        > 0.5f => Color.LimeGreen,
        > 0f => Color.Green,
        > -0.5f => Color.Orange,
        _ => Color.Red
    };

    private static Color GetHealthColor(int health) => health switch
    {
        >= 75 => Color.LimeGreen,
        >= 50 => Color.Yellow,
        >= 25 => Color.Orange,
        _ => Color.Red
    };

    public void Dispose()
    {
        _backGraphics.Dispose();
        _frontBuffer.Dispose();
        _backBuffer.Dispose();
    }
}

/// <summary>
/// Detection data for smooth rendering.
/// </summary>
public sealed class DetectionData
{
    public int TrackId { get; init; }
    public BoundingBox Box { get; init; }
    public float Confidence { get; init; }
    public string ClassName { get; init; } = "";
    public bool IsThreat { get; init; }
    public Vector2 Velocity { get; init; }
}

// Internal classes for smooth animation
internal sealed class SmoothDetection
{
    public int TrackId { get; init; }
    public BoundingBox CurrentBox { get; set; }
    public BoundingBox TargetBox { get; set; }
    public float CurrentConfidence { get; set; }
    public float TargetConfidence { get; set; }
    public string ClassName { get; set; } = "";
    public bool IsThreat { get; set; }
    public Vector2 Velocity { get; set; }
    public DateTime LastSeen { get; set; }
    public float Alpha { get; set; } = 1f;
    public float TargetAlpha { get; set; } = 1f;
}

internal sealed class FadingDetection
{
    public BoundingBox Box { get; init; }
    public string ClassName { get; init; } = "";
    public float Alpha { get; set; }
    public Color Color { get; init; }
}

internal sealed class SmoothMarker
{
    public Vector2 Position { get; init; }
    public string Label { get; init; } = "";
    public Color Color { get; init; }
    public float Alpha { get; set; }
    public float Duration { get; init; }
    public DateTime CreatedAt { get; init; }
}

internal sealed class SmoothPath
{
    public List<Vector2> Waypoints { get; init; } = new();
    public Color Color { get; init; }
    public int CurrentIndex { get; set; }
    public float Alpha { get; set; }
}
