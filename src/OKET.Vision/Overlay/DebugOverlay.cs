using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using OKET.Core.Navigation;
using OKET.Core.Types;
using OKET.Core.Policy;
using DetectionClass = OKET.Core.Detection.Detection;
using DetectionResult = OKET.Core.Detection.DetectionResult;

namespace OKET.Vision.Overlay;

/// <summary>
/// Debug overlay for visualizing navigation, paths, and agent state.
/// Uses GDI+ to draw on a transparent window overlay.
/// </summary>
public sealed class DebugOverlay : IDisposable
{
    private readonly IntPtr _targetWindow;
    private readonly int _width;
    private readonly int _height;
    private bool _isEnabled = true;
    private bool _disposed;

    // Drawing state
    private readonly List<PathVisualization> _paths = new();
    private readonly List<NavMeshVisualization> _navMeshes = new();
    private readonly List<MarkerVisualization> _markers = new();
    private readonly List<TextVisualization> _texts = new();
    private readonly List<DetectionVisualization> _detections = new();

    // Debug state (shown in corner panel)
    private DebugState _debugState = new();

    // Colors
    private static readonly Color PathColor = Color.FromArgb(180, 0, 150, 255); // Blue
    private static readonly Color NavMeshColor = Color.FromArgb(80, 0, 255, 100); // Green
    private static readonly Color WaypointColor = Color.FromArgb(200, 255, 255, 0); // Yellow
    private static readonly Color TargetColor = Color.FromArgb(200, 255, 0, 0); // Red
    private static readonly Color PlayerColor = Color.FromArgb(200, 0, 255, 255); // Cyan
    private static readonly Color HallwayColor = Color.FromArgb(100, 255, 165, 0); // Orange
    private static readonly Color CoverColor = Color.FromArgb(100, 128, 0, 128); // Purple

    // Detection colors by class
    private static readonly Color ThreatColor = Color.FromArgb(220, 255, 50, 50); // Red
    private static readonly Color ItemColor = Color.FromArgb(220, 50, 255, 50); // Green
    private static readonly Color SurvivorColor = Color.FromArgb(220, 50, 150, 255); // Blue
    private static readonly Color UnknownColor = Color.FromArgb(180, 200, 200, 200); // Gray

    // Panel colors
    private static readonly Color PanelBackground = Color.FromArgb(200, 20, 20, 20);
    private static readonly Color PanelBorder = Color.FromArgb(200, 60, 60, 60);

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public DebugOverlay(IntPtr targetWindow, int width = 1920, int height = 1080)
    {
        _targetWindow = targetWindow;
        _width = width;
        _height = height;
    }

    /// <summary>
    /// Add a path to visualize.
    /// </summary>
    public void AddPath(List<Vector2> waypoints, Color? color = null, float thickness = 3f)
    {
        if (waypoints.Count < 2) return;

        _paths.Add(new PathVisualization
        {
            Waypoints = waypoints.ToList(),
            Color = color ?? PathColor,
            Thickness = thickness,
            CreatedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Add a path from NavNodes.
    /// </summary>
    public void AddPath(List<NavNode> nodes, Color? color = null, float thickness = 3f)
    {
        AddPath(nodes.Select(n => n.Position).ToList(), color, thickness);
    }

    /// <summary>
    /// Set the current navigation path (clears previous).
    /// </summary>
    public void SetCurrentPath(List<NavNode>? path, int currentIndex = 0)
    {
        // Remove old current path
        _paths.RemoveAll(p => p.IsCurrent);

        if (path == null || path.Count < 2)
            return;

        var waypoints = path.Select(n => n.Position).ToList();

        _paths.Add(new PathVisualization
        {
            Waypoints = waypoints,
            Color = PathColor,
            Thickness = 4f,
            IsCurrent = true,
            CurrentIndex = currentIndex,
            CreatedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Add navmesh visualization.
    /// </summary>
    public void SetNavMesh(NavMesh navMesh)
    {
        _navMeshes.Clear();

        var edges = navMesh.GetWalkableEdges();
        _navMeshes.Add(new NavMeshVisualization
        {
            Edges = edges,
            Color = NavMeshColor
        });
    }

    /// <summary>
    /// Add a marker at a position.
    /// </summary>
    public void AddMarker(Vector2 position, MarkerType type, string? label = null, float duration = 2f)
    {
        _markers.Add(new MarkerVisualization
        {
            Position = position,
            Type = type,
            Label = label,
            Color = GetMarkerColor(type),
            CreatedAt = DateTime.UtcNow,
            Duration = TimeSpan.FromSeconds(duration)
        });
    }

    /// <summary>
    /// Add text at a position.
    /// </summary>
    public void AddText(Vector2 position, string text, Color? color = null, float duration = 1f)
    {
        _texts.Add(new TextVisualization
        {
            Position = position,
            Text = text,
            Color = color ?? Color.White,
            CreatedAt = DateTime.UtcNow,
            Duration = TimeSpan.FromSeconds(duration)
        });
    }

    /// <summary>
    /// Update detections from game state. Call this every frame.
    /// </summary>
    public void UpdateDetections(IEnumerable<DetectionClass> detections)
    {
        _detections.Clear();

        foreach (var det in detections)
        {
            var className = det.Class.ToString();
            _detections.Add(new DetectionVisualization
            {
                TrackId = det.TrackId,
                ClassName = className,
                Confidence = det.Confidence,
                Box = det.Box,
                Velocity = det.Velocity ?? default,
                Priority = det.Priority,
                IsThreat = det.IsThreat,
                Color = GetDetectionColor(className)
            });
        }
    }

    /// <summary>
    /// Update detections from a DetectionResult.
    /// </summary>
    public void UpdateDetections(DetectionResult detectionResult)
    {
        UpdateDetections(detectionResult.Detections);
    }

    /// <summary>
    /// Add a single detection.
    /// </summary>
    public void AddDetection(DetectionClass detection)
    {
        var className = detection.Class.ToString();
        _detections.Add(new DetectionVisualization
        {
            TrackId = detection.TrackId,
            ClassName = className,
            Confidence = detection.Confidence,
            Box = detection.Box,
            Velocity = detection.Velocity ?? default,
            Priority = detection.Priority,
            IsThreat = detection.IsThreat,
            Color = GetDetectionColor(className)
        });
    }

    /// <summary>
    /// Render the overlay to a bitmap.
    /// </summary>
    public Bitmap Render()
    {
        var bitmap = new Bitmap(_width, _height);

        if (!_isEnabled)
            return bitmap;

        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        var now = DateTime.UtcNow;

        // Draw navmesh
        foreach (var navMesh in _navMeshes)
        {
            DrawNavMesh(graphics, navMesh);
        }

        // Draw paths
        foreach (var path in _paths.ToList())
        {
            DrawPath(graphics, path);
        }

        // Draw markers (and remove expired)
        foreach (var marker in _markers.ToList())
        {
            if (now - marker.CreatedAt > marker.Duration)
            {
                _markers.Remove(marker);
                continue;
            }
            DrawMarker(graphics, marker, now);
        }

        // Draw text (and remove expired)
        foreach (var text in _texts.ToList())
        {
            if (now - text.CreatedAt > text.Duration)
            {
                _texts.Remove(text);
                continue;
            }
            DrawText(graphics, text);
        }

        // Draw detections (moving objects)
        foreach (var detection in _detections)
        {
            DrawDetection(graphics, detection);
        }

        // Draw debug panel (top-left corner)
        DrawDebugPanel(graphics);

        return bitmap;
    }

    /// <summary>
    /// Update debug state for overlay panel.
    /// </summary>
    public void UpdateDebugState(DebugState state)
    {
        _debugState = state;
    }

    private void DrawDebugPanel(Graphics g)
    {
        const int panelX = 10;
        const int panelY = 10;
        const int panelWidth = 280;
        const int lineHeight = 18;
        const int padding = 8;

        var lines = new List<(string text, Color color)>
        {
            ($"OKET AGI v0.1", Color.Cyan),
            ($"─────────────────────────", Color.Gray),
            ($"Intent: {_debugState.IntentType}", GetIntentColor(_debugState.IntentType)),
            ($"  └─ {_debugState.IntentReason}", Color.LightGray),
            ($"Confidence: {_debugState.Confidence:P0}", GetConfidenceColor(_debugState.Confidence)),
            ($"─────────────────────────", Color.Gray),
            ($"Skill: {_debugState.ActiveSkill}", Color.White),
            ($"Action: {_debugState.ChosenAction}", Color.Yellow),
            ($"─────────────────────────", Color.Gray),
            ($"Prediction Error: {_debugState.PredictionError:F1}px", GetErrorColor(_debugState.PredictionError)),
            ($"Reward: {_debugState.LastReward:+0.00;-0.00}", GetRewardColor(_debugState.LastReward)),
            ($"─────────────────────────", Color.Gray),
            ($"Threats: {_debugState.ThreatCount}", _debugState.ThreatCount > 0 ? Color.Red : Color.Green),
            ($"Health: {_debugState.Health}%", GetHealthColor(_debugState.Health)),
            ($"FPS: {_debugState.Fps:F0}", Color.White)
        };

        int panelHeight = (lines.Count * lineHeight) + (padding * 2);

        // Draw panel background
        using var bgBrush = new SolidBrush(PanelBackground);
        using var borderPen = new Pen(PanelBorder, 1);
        g.FillRectangle(bgBrush, panelX, panelY, panelWidth, panelHeight);
        g.DrawRectangle(borderPen, panelX, panelY, panelWidth, panelHeight);

        // Draw lines
        using var font = new Font("Consolas", 10, FontStyle.Regular);
        int y = panelY + padding;

        foreach (var (text, color) in lines)
        {
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, panelX + padding, y);
            y += lineHeight;
        }

        // Draw confidence bar
        int barX = panelX + padding;
        int barY = panelY + panelHeight + 5;
        int barWidth = panelWidth - (padding * 2);
        int barHeight = 6;

        using var barBgBrush = new SolidBrush(Color.FromArgb(100, 40, 40, 40));
        g.FillRectangle(barBgBrush, barX, barY, barWidth, barHeight);

        int fillWidth = (int)(barWidth * Math.Clamp(_debugState.Confidence, 0f, 1f));
        using var barFillBrush = new SolidBrush(GetConfidenceColor(_debugState.Confidence));
        g.FillRectangle(barFillBrush, barX, barY, fillWidth, barHeight);
    }

    private static Color GetIntentColor(string intentType)
    {
        return intentType.ToLower() switch
        {
            "idle" => Color.Gray,
            "survive" => Color.Yellow,
            "engage" => Color.Red,
            "navigate" => Color.Cyan,
            "retreat" => Color.Orange,
            "acquire" => Color.Green,
            _ => Color.White
        };
    }

    private static Color GetConfidenceColor(float confidence)
    {
        if (confidence >= 0.8f) return Color.LimeGreen;
        if (confidence >= 0.5f) return Color.Yellow;
        if (confidence >= 0.3f) return Color.Orange;
        return Color.Red;
    }

    private static Color GetErrorColor(float error)
    {
        if (error < 10f) return Color.LimeGreen;
        if (error < 30f) return Color.Yellow;
        if (error < 50f) return Color.Orange;
        return Color.Red;
    }

    private static Color GetRewardColor(float reward)
    {
        if (reward > 0.5f) return Color.LimeGreen;
        if (reward > 0f) return Color.Green;
        if (reward > -0.5f) return Color.Orange;
        return Color.Red;
    }

    private static Color GetHealthColor(int health)
    {
        if (health >= 75) return Color.LimeGreen;
        if (health >= 50) return Color.Yellow;
        if (health >= 25) return Color.Orange;
        return Color.Red;
    }

    /// <summary>
    /// Draw overlay directly to screen (for direct rendering mode).
    /// </summary>
    public void RenderToScreen()
    {
        if (!_isEnabled || _targetWindow == IntPtr.Zero)
            return;

        using var bitmap = Render();
        using var graphics = Graphics.FromHwnd(_targetWindow);

        graphics.DrawImage(bitmap, 0, 0);
    }

    private void DrawNavMesh(Graphics g, NavMeshVisualization navMesh)
    {
        using var pen = new Pen(navMesh.Color, 1f);
        pen.DashStyle = DashStyle.Dot;

        foreach (var (from, to) in navMesh.Edges)
        {
            g.DrawLine(pen, from.X, from.Y, to.X, to.Y);
        }
    }

    private void DrawPath(Graphics g, PathVisualization path)
    {
        if (path.Waypoints.Count < 2)
            return;

        using var pen = new Pen(path.Color, path.Thickness);
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.ArrowAnchor;

        // Draw path segments
        for (int i = 0; i < path.Waypoints.Count - 1; i++)
        {
            var from = path.Waypoints[i];
            var to = path.Waypoints[i + 1];

            // Dim already-traversed segments
            if (path.IsCurrent && i < path.CurrentIndex)
            {
                using var dimPen = new Pen(Color.FromArgb(80, path.Color), path.Thickness * 0.5f);
                g.DrawLine(dimPen, from.X, from.Y, to.X, to.Y);
            }
            else
            {
                g.DrawLine(pen, from.X, from.Y, to.X, to.Y);
            }
        }

        // Draw waypoint circles
        using var waypointBrush = new SolidBrush(WaypointColor);
        foreach (var wp in path.Waypoints)
        {
            g.FillEllipse(waypointBrush, wp.X - 5, wp.Y - 5, 10, 10);
        }

        // Draw current position indicator
        if (path.IsCurrent && path.CurrentIndex < path.Waypoints.Count)
        {
            var current = path.Waypoints[path.CurrentIndex];
            using var currentBrush = new SolidBrush(Color.FromArgb(255, 0, 255, 0));
            g.FillEllipse(currentBrush, current.X - 8, current.Y - 8, 16, 16);
        }
    }

    private void DrawMarker(Graphics g, MarkerVisualization marker, DateTime now)
    {
        float elapsed = (float)(now - marker.CreatedAt).TotalSeconds;
        float remaining = (float)marker.Duration.TotalSeconds - elapsed;
        float alpha = Math.Min(1f, remaining * 2); // Fade out

        var color = Color.FromArgb((int)(marker.Color.A * alpha), marker.Color);

        using var brush = new SolidBrush(color);
        using var pen = new Pen(color, 2f);

        float size = GetMarkerSize(marker.Type);

        switch (marker.Type)
        {
            case MarkerType.Waypoint:
                g.FillEllipse(brush, marker.Position.X - size / 2, marker.Position.Y - size / 2, size, size);
                break;

            case MarkerType.Target:
                // Crosshair
                g.DrawLine(pen, marker.Position.X - size, marker.Position.Y, marker.Position.X + size, marker.Position.Y);
                g.DrawLine(pen, marker.Position.X, marker.Position.Y - size, marker.Position.X, marker.Position.Y + size);
                g.DrawEllipse(pen, marker.Position.X - size / 2, marker.Position.Y - size / 2, size, size);
                break;

            case MarkerType.Player:
                // Diamond
                var points = new PointF[]
                {
                    new(marker.Position.X, marker.Position.Y - size),
                    new(marker.Position.X + size, marker.Position.Y),
                    new(marker.Position.X, marker.Position.Y + size),
                    new(marker.Position.X - size, marker.Position.Y)
                };
                g.FillPolygon(brush, points);
                break;

            case MarkerType.Hazard:
                // Triangle
                var triPoints = new PointF[]
                {
                    new(marker.Position.X, marker.Position.Y - size),
                    new(marker.Position.X + size, marker.Position.Y + size),
                    new(marker.Position.X - size, marker.Position.Y + size)
                };
                g.DrawPolygon(pen, triPoints);
                break;

            default:
                g.FillRectangle(brush, marker.Position.X - size / 2, marker.Position.Y - size / 2, size, size);
                break;
        }

        // Draw label
        if (!string.IsNullOrEmpty(marker.Label))
        {
            using var font = new Font("Arial", 10, FontStyle.Bold);
            using var labelBrush = new SolidBrush(Color.White);
            g.DrawString(marker.Label, font, labelBrush, marker.Position.X + size, marker.Position.Y - size);
        }
    }

    private static void DrawText(Graphics g, TextVisualization text)
    {
        using var font = new Font("Arial", 12, FontStyle.Bold);
        using var brush = new SolidBrush(text.Color);
        using var shadowBrush = new SolidBrush(Color.FromArgb(150, 0, 0, 0));

        // Shadow
        g.DrawString(text.Text, font, shadowBrush, text.Position.X + 2, text.Position.Y + 2);
        // Text
        g.DrawString(text.Text, font, brush, text.Position.X, text.Position.Y);
    }

    private static Color GetMarkerColor(MarkerType type)
    {
        return type switch
        {
            MarkerType.Waypoint => WaypointColor,
            MarkerType.Target => TargetColor,
            MarkerType.Player => PlayerColor,
            MarkerType.Hallway => HallwayColor,
            MarkerType.Cover => CoverColor,
            MarkerType.Hazard => Color.Red,
            _ => Color.White
        };
    }

    private static Color GetDetectionColor(string detectionClass)
    {
        var lowerClass = detectionClass.ToLowerInvariant();

        // Threats (zombies, enemies, etc.)
        if (lowerClass.Contains("zombie") || lowerClass.Contains("enemy") ||
            lowerClass.Contains("threat") || lowerClass.Contains("hostile"))
        {
            return ThreatColor;
        }

        // Items (loot, weapons, supplies)
        if (lowerClass.Contains("item") || lowerClass.Contains("loot") ||
            lowerClass.Contains("weapon") || lowerClass.Contains("supply") ||
            lowerClass.Contains("ammo") || lowerClass.Contains("health"))
        {
            return ItemColor;
        }

        // Survivors/players
        if (lowerClass.Contains("survivor") || lowerClass.Contains("player") ||
            lowerClass.Contains("teammate") || lowerClass.Contains("ally"))
        {
            return SurvivorColor;
        }

        return UnknownColor;
    }

    private void DrawDetection(Graphics g, DetectionVisualization detection)
    {
        var box = detection.Box;

        // Draw bounding box
        using var pen = new Pen(detection.Color, 2f);
        g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);

        // Draw corner accents for better visibility
        float cornerSize = Math.Min(box.Width, box.Height) * 0.2f;
        using var accentPen = new Pen(detection.Color, 3f);

        // Top-left corner
        g.DrawLine(accentPen, box.X, box.Y, box.X + cornerSize, box.Y);
        g.DrawLine(accentPen, box.X, box.Y, box.X, box.Y + cornerSize);

        // Top-right corner
        g.DrawLine(accentPen, box.X + box.Width, box.Y, box.X + box.Width - cornerSize, box.Y);
        g.DrawLine(accentPen, box.X + box.Width, box.Y, box.X + box.Width, box.Y + cornerSize);

        // Bottom-left corner
        g.DrawLine(accentPen, box.X, box.Y + box.Height, box.X + cornerSize, box.Y + box.Height);
        g.DrawLine(accentPen, box.X, box.Y + box.Height, box.X, box.Y + box.Height - cornerSize);

        // Bottom-right corner
        g.DrawLine(accentPen, box.X + box.Width, box.Y + box.Height, box.X + box.Width - cornerSize, box.Y + box.Height);
        g.DrawLine(accentPen, box.X + box.Width, box.Y + box.Height, box.X + box.Width, box.Y + box.Height - cornerSize);

        // Draw label background
        string label = $"{detection.ClassName} ({detection.Confidence:P0})";
        if (detection.TrackId > 0)
        {
            label = $"[{detection.TrackId}] {label}";
        }

        using var font = new Font("Arial", 10, FontStyle.Bold);
        var labelSize = g.MeasureString(label, font);

        float labelX = box.X;
        float labelY = box.Y - labelSize.Height - 2;
        if (labelY < 0) labelY = box.Y + box.Height + 2; // Put below if no room above

        // Draw label background
        using var bgBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
        g.FillRectangle(bgBrush, labelX, labelY, labelSize.Width + 4, labelSize.Height);

        // Draw label text
        using var textBrush = new SolidBrush(detection.Color);
        g.DrawString(label, font, textBrush, labelX + 2, labelY);

        // Draw velocity arrow if moving
        if (detection.Velocity.X != 0 || detection.Velocity.Y != 0)
        {
            var centerX = box.X + box.Width / 2;
            var centerY = box.Y + box.Height / 2;

            // Scale velocity for visualization
            float velocityScale = 20f;
            var endX = centerX + detection.Velocity.X * velocityScale;
            var endY = centerY + detection.Velocity.Y * velocityScale;

            using var velocityPen = new Pen(Color.FromArgb(200, 255, 255, 255), 2f);
            velocityPen.EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor;
            g.DrawLine(velocityPen, centerX, centerY, endX, endY);
        }

        // Draw threat indicator
        if (detection.IsThreat)
        {
            using var threatBrush = new SolidBrush(Color.FromArgb(150, 255, 0, 0));
            float indicatorSize = 8;
            g.FillEllipse(threatBrush, box.X - indicatorSize - 2, box.Y, indicatorSize, indicatorSize);
        }

        // Draw priority indicator
        if (detection.Priority > 0.7f)
        {
            using var priorityPen = new Pen(Color.FromArgb(200, 255, 215, 0), 2f); // Gold
            g.DrawRectangle(priorityPen, box.X - 2, box.Y - 2, box.Width + 4, box.Height + 4);
        }
    }

    private static float GetMarkerSize(MarkerType type)
    {
        return type switch
        {
            MarkerType.Target => 20f,
            MarkerType.Player => 15f,
            MarkerType.Hazard => 18f,
            _ => 10f
        };
    }

    /// <summary>
    /// Clear all visualizations.
    /// </summary>
    public void Clear()
    {
        _paths.Clear();
        _navMeshes.Clear();
        _markers.Clear();
        _texts.Clear();
        _detections.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
    }
}

/// <summary>
/// Types of markers.
/// </summary>
public enum MarkerType
{
    Waypoint,
    Target,
    Player,
    Hallway,
    Cover,
    Hazard,
    Objective,
    Custom
}

internal class PathVisualization
{
    public List<Vector2> Waypoints { get; init; } = new();
    public Color Color { get; init; }
    public float Thickness { get; init; }
    public bool IsCurrent { get; init; }
    public int CurrentIndex { get; set; }
    public DateTime CreatedAt { get; init; }
}

internal class NavMeshVisualization
{
    public List<(Vector2 From, Vector2 To)> Edges { get; init; } = new();
    public Color Color { get; init; }
}

internal class MarkerVisualization
{
    public Vector2 Position { get; init; }
    public MarkerType Type { get; init; }
    public string? Label { get; init; }
    public Color Color { get; init; }
    public DateTime CreatedAt { get; init; }
    public TimeSpan Duration { get; init; }
}

internal class TextVisualization
{
    public Vector2 Position { get; init; }
    public string Text { get; init; } = "";
    public Color Color { get; init; }
    public DateTime CreatedAt { get; init; }
    public TimeSpan Duration { get; init; }
}

internal class DetectionVisualization
{
    public int TrackId { get; init; }
    public string ClassName { get; init; } = "";
    public float Confidence { get; init; }
    public BoundingBox Box { get; init; }
    public Vector2 Velocity { get; init; }
    public float Priority { get; init; }
    public bool IsThreat { get; init; }
    public Color Color { get; init; }
}

/// <summary>
/// Debug state for overlay panel display.
/// If it's not visible, it's not debuggable.
/// </summary>
public sealed class DebugState
{
    /// <summary>Current intent type name.</summary>
    public string IntentType { get; init; } = "Idle";

    /// <summary>Reason for current intent.</summary>
    public string IntentReason { get; init; } = "";

    /// <summary>Overall confidence [0, 1].</summary>
    public float Confidence { get; init; } = 1f;

    /// <summary>Currently active skill name.</summary>
    public string ActiveSkill { get; init; } = "None";

    /// <summary>Chosen action description.</summary>
    public string ChosenAction { get; init; } = "None";

    /// <summary>Last prediction error in pixels.</summary>
    public float PredictionError { get; init; }

    /// <summary>Last frame's total reward.</summary>
    public float LastReward { get; init; }

    /// <summary>Current threat count.</summary>
    public int ThreatCount { get; init; }

    /// <summary>Current health percentage.</summary>
    public int Health { get; init; } = 100;

    /// <summary>Current FPS.</summary>
    public float Fps { get; init; }

    /// <summary>Navigation policy status.</summary>
    public string NavStatus { get; init; } = "Idle";

    /// <summary>Distance to current goal.</summary>
    public float DistanceToGoal { get; init; }
}
