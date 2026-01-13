using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using OKET.Core.Navigation;
using OKET.Core.Types;

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

    // Colors
    private static readonly Color PathColor = Color.FromArgb(180, 0, 150, 255); // Blue
    private static readonly Color NavMeshColor = Color.FromArgb(80, 0, 255, 100); // Green
    private static readonly Color WaypointColor = Color.FromArgb(200, 255, 255, 0); // Yellow
    private static readonly Color TargetColor = Color.FromArgb(200, 255, 0, 0); // Red
    private static readonly Color PlayerColor = Color.FromArgb(200, 0, 255, 255); // Cyan
    private static readonly Color HallwayColor = Color.FromArgb(100, 255, 165, 0); // Orange
    private static readonly Color CoverColor = Color.FromArgb(100, 128, 0, 128); // Purple

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

        return bitmap;
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
