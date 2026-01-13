using System.Drawing;
using System.Drawing.Drawing2D;
using OKET.Core.Types;

namespace OKET.Core.Intelligence;

/// <summary>
/// Renders intelligent detections with YOLO-style bounding boxes.
///
/// Features:
/// - Clean, modern box rendering with corner accents
/// - Confidence bars
/// - Velocity arrows
/// - Threat/opportunity indicators
/// - Knowledge tags
/// - Priority glow effects
/// - Trust level badges
///
/// This is what makes detections VISIBLE and UNDERSTANDABLE.
/// </summary>
public sealed class IntelligenceRenderer
{
    // Style configuration
    private readonly RenderStyle _style;

    // Cached resources
    private Font? _labelFont;
    private Font? _tagFont;
    private Font? _smallFont;

    public IntelligenceRenderer(RenderStyle? style = null)
    {
        _style = style ?? RenderStyle.Default;
    }

    /// <summary>
    /// Render all detections to a bitmap.
    /// </summary>
    public Bitmap RenderDetections(
        IEnumerable<IntelligentDetection> detections,
        int width,
        int height,
        IntelligenceFrame? frame = null)
    {
        var bitmap = new Bitmap(width, height);

        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        // Initialize fonts
        _labelFont ??= new Font(_style.FontFamily, _style.LabelFontSize, FontStyle.Bold);
        _tagFont ??= new Font(_style.FontFamily, _style.TagFontSize, FontStyle.Regular);
        _smallFont ??= new Font(_style.FontFamily, _style.SmallFontSize, FontStyle.Regular);

        // Render in priority order (highest priority last = on top)
        var sorted = detections.OrderBy(d => d.Priority).ToList();

        foreach (var detection in sorted)
        {
            RenderDetection(g, detection);
        }

        // Render frame overlay if provided
        if (frame != null)
        {
            RenderFrameInfo(g, frame, width, height);
        }

        return bitmap;
    }

    /// <summary>
    /// Render directly to a graphics context.
    /// </summary>
    public void RenderTo(
        Graphics g,
        IEnumerable<IntelligentDetection> detections,
        IntelligenceFrame? frame = null,
        int width = 0,
        int height = 0)
    {
        _labelFont ??= new Font(_style.FontFamily, _style.LabelFontSize, FontStyle.Bold);
        _tagFont ??= new Font(_style.FontFamily, _style.TagFontSize, FontStyle.Regular);
        _smallFont ??= new Font(_style.FontFamily, _style.SmallFontSize, FontStyle.Regular);

        var sorted = detections.OrderBy(d => d.Priority).ToList();

        foreach (var detection in sorted)
        {
            RenderDetection(g, detection);
        }

        if (frame != null && width > 0 && height > 0)
        {
            RenderFrameInfo(g, frame, width, height);
        }
    }

    /// <summary>
    /// Render a single detection.
    /// </summary>
    private void RenderDetection(Graphics g, IntelligentDetection detection)
    {
        var box = detection.BoundingBox;
        var color = detection.RenderColor;
        float alpha = detection.RenderAlpha;

        // Apply alpha to color
        color = Color.FromArgb((int)(255 * alpha), color);

        // === GLOW EFFECT FOR HIGH PRIORITY ===
        if (_style.EnableGlow && detection.Priority > 0.7f)
        {
            RenderGlow(g, box, color, detection.Priority);
        }

        // === MAIN BOUNDING BOX ===
        if (_style.BoxStyle == BoxDrawStyle.Solid)
        {
            RenderSolidBox(g, box, color);
        }
        else
        {
            RenderCornerBox(g, box, color);
        }

        // === LABEL WITH BACKGROUND ===
        RenderLabel(g, detection, box, color);

        // === CONFIDENCE BAR ===
        if (_style.ShowConfidenceBar)
        {
            RenderConfidenceBar(g, box, detection.Confidence, color);
        }

        // === VELOCITY ARROW ===
        if (_style.ShowVelocity && detection.IsMoving)
        {
            RenderVelocityArrow(g, box, detection.Velocity, color);
        }

        // === THREAT/OPPORTUNITY INDICATORS ===
        if (_style.ShowIndicators)
        {
            RenderIndicators(g, detection, box);
        }

        // === KNOWLEDGE TAGS ===
        if (_style.ShowTags && detection.Tags.Count > 0)
        {
            RenderTags(g, detection.Tags, box);
        }

        // === TRUST BADGE ===
        if (_style.ShowTrustBadge && detection.TrustLevel >= TrustLevel.Certified)
        {
            RenderTrustBadge(g, detection.TrustLevel, box);
        }

        // === AIM POINT ===
        if (_style.ShowAimPoint && detection.IsThreat)
        {
            RenderAimPoint(g, detection, color);
        }
    }

    /// <summary>
    /// Render glow effect around box.
    /// </summary>
    private void RenderGlow(Graphics g, BoundingBox box, Color color, float intensity)
    {
        int glowSize = (int)(_style.GlowSize * intensity);
        var glowColor = Color.FromArgb((int)(40 * intensity), color);

        for (int i = glowSize; i > 0; i--)
        {
            using var pen = new Pen(Color.FromArgb(
                (int)(glowColor.A * (1 - (float)i / glowSize)),
                glowColor.R, glowColor.G, glowColor.B), 2);

            g.DrawRectangle(pen,
                box.X - i, box.Y - i,
                box.Width + i * 2, box.Height + i * 2);
        }
    }

    /// <summary>
    /// Render solid bounding box.
    /// </summary>
    private void RenderSolidBox(Graphics g, BoundingBox box, Color color)
    {
        using var pen = new Pen(color, _style.BoxThickness);
        g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
    }

    /// <summary>
    /// Render box with corner accents (YOLO style).
    /// </summary>
    private void RenderCornerBox(Graphics g, BoundingBox box, Color color)
    {
        float cornerLen = Math.Min(box.Width, box.Height) * _style.CornerRatio;
        cornerLen = Math.Max(cornerLen, 8);

        // Thin border
        using var thinPen = new Pen(Color.FromArgb(100, color), 1);
        g.DrawRectangle(thinPen, box.X, box.Y, box.Width, box.Height);

        // Thick corner accents
        using var thickPen = new Pen(color, _style.CornerThickness);
        thickPen.StartCap = LineCap.Round;
        thickPen.EndCap = LineCap.Round;

        // Top-left corner
        g.DrawLine(thickPen, box.X, box.Y, box.X + cornerLen, box.Y);
        g.DrawLine(thickPen, box.X, box.Y, box.X, box.Y + cornerLen);

        // Top-right corner
        g.DrawLine(thickPen, box.X + box.Width, box.Y, box.X + box.Width - cornerLen, box.Y);
        g.DrawLine(thickPen, box.X + box.Width, box.Y, box.X + box.Width, box.Y + cornerLen);

        // Bottom-left corner
        g.DrawLine(thickPen, box.X, box.Y + box.Height, box.X + cornerLen, box.Y + box.Height);
        g.DrawLine(thickPen, box.X, box.Y + box.Height, box.X, box.Y + box.Height - cornerLen);

        // Bottom-right corner
        g.DrawLine(thickPen, box.X + box.Width, box.Y + box.Height, box.X + box.Width - cornerLen, box.Y + box.Height);
        g.DrawLine(thickPen, box.X + box.Width, box.Y + box.Height, box.X + box.Width, box.Y + box.Height - cornerLen);
    }

    /// <summary>
    /// Render detection label with background.
    /// </summary>
    private void RenderLabel(Graphics g, IntelligentDetection detection, BoundingBox box, Color color)
    {
        // Build label text
        string label = detection.ClassName;
        if (_style.ShowTrackId)
            label = $"[{detection.TrackId}] {label}";
        if (_style.ShowConfidenceInLabel)
            label += $" {detection.Confidence:P0}";

        var labelSize = g.MeasureString(label, _labelFont!);

        // Position label (above or below box)
        float labelX = box.X;
        float labelY = box.Y - labelSize.Height - 2;
        if (labelY < 0)
            labelY = box.Y + box.Height + 2;

        // Draw background
        using var bgBrush = new SolidBrush(Color.FromArgb(_style.LabelBackgroundAlpha, 0, 0, 0));
        g.FillRectangle(bgBrush, labelX - 2, labelY - 1, labelSize.Width + 4, labelSize.Height + 2);

        // Draw text
        using var textBrush = new SolidBrush(color);
        g.DrawString(label, _labelFont!, textBrush, labelX, labelY);
    }

    /// <summary>
    /// Render confidence bar below box.
    /// </summary>
    private void RenderConfidenceBar(Graphics g, BoundingBox box, float confidence, Color color)
    {
        float barHeight = 4;
        float barY = box.Y + box.Height + 2;

        // Background
        using var bgBrush = new SolidBrush(Color.FromArgb(100, 40, 40, 40));
        g.FillRectangle(bgBrush, box.X, barY, box.Width, barHeight);

        // Fill
        float fillWidth = box.Width * confidence;
        using var fillBrush = new SolidBrush(color);
        g.FillRectangle(fillBrush, box.X, barY, fillWidth, barHeight);
    }

    /// <summary>
    /// Render velocity arrow.
    /// </summary>
    private void RenderVelocityArrow(Graphics g, BoundingBox box, Vector2 velocity, Color color)
    {
        float cx = box.X + box.Width / 2;
        float cy = box.Y + box.Height / 2;

        float scale = _style.VelocityScale;
        float ex = cx + velocity.X * scale;
        float ey = cy + velocity.Y * scale;

        using var pen = new Pen(Color.FromArgb(200, 255, 255, 255), 2);
        pen.EndCap = LineCap.ArrowAnchor;
        g.DrawLine(pen, cx, cy, ex, ey);
    }

    /// <summary>
    /// Render threat/opportunity indicators.
    /// </summary>
    private void RenderIndicators(Graphics g, IntelligentDetection detection, BoundingBox box)
    {
        float indicatorSize = 8;
        float x = box.X - indicatorSize - 4;
        float y = box.Y;

        // Threat indicator (red dot)
        if (detection.IsThreat)
        {
            using var brush = new SolidBrush(Color.FromArgb(200, 255, 50, 50));
            g.FillEllipse(brush, x, y, indicatorSize, indicatorSize);
        }

        // Opportunity indicator (green dot)
        if (detection.IsOpportunity)
        {
            using var brush = new SolidBrush(Color.FromArgb(200, 50, 255, 50));
            g.FillEllipse(brush, x, y + indicatorSize + 2, indicatorSize, indicatorSize);
        }

        // Approaching indicator (yellow arrow)
        if (detection.IsApproaching)
        {
            using var brush = new SolidBrush(Color.FromArgb(200, 255, 200, 0));
            var points = new PointF[]
            {
                new(x + indicatorSize / 2, y + indicatorSize * 2 + 4),
                new(x, y + indicatorSize * 2 + 12),
                new(x + indicatorSize, y + indicatorSize * 2 + 12)
            };
            g.FillPolygon(brush, points);
        }
    }

    /// <summary>
    /// Render knowledge tags.
    /// </summary>
    private void RenderTags(Graphics g, List<KnowledgeTag> tags, BoundingBox box)
    {
        float y = box.Y + box.Height + 10;
        float x = box.X;

        foreach (var tag in tags.Take(3)) // Max 3 tags
        {
            var tagSize = g.MeasureString(tag.Name, _tagFont!);

            // Tag background
            var tagColor = GetTagColor(tag.Category);
            using var bgBrush = new SolidBrush(Color.FromArgb(180, tagColor));
            g.FillRectangle(bgBrush, x, y, tagSize.Width + 4, tagSize.Height);

            // Tag text
            using var textBrush = new SolidBrush(Color.White);
            g.DrawString(tag.Name, _tagFont!, textBrush, x + 2, y);

            x += tagSize.Width + 6;
        }
    }

    /// <summary>
    /// Render trust badge.
    /// </summary>
    private void RenderTrustBadge(Graphics g, TrustLevel level, BoundingBox box)
    {
        string badge = level switch
        {
            TrustLevel.Certified => "C",
            TrustLevel.Trusted => "T",
            TrustLevel.Absolute => "A",
            _ => ""
        };

        if (string.IsNullOrEmpty(badge)) return;

        var color = level switch
        {
            TrustLevel.Certified => Color.FromArgb(200, 50, 150, 255),
            TrustLevel.Trusted => Color.FromArgb(200, 50, 255, 150),
            TrustLevel.Absolute => Color.FromArgb(200, 255, 215, 0),
            _ => Color.Gray
        };

        float badgeSize = 16;
        float x = box.X + box.Width - badgeSize + 4;
        float y = box.Y - 4;

        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, x, y, badgeSize, badgeSize);

        using var textBrush = new SolidBrush(Color.White);
        g.DrawString(badge, _smallFont!, textBrush, x + 4, y + 1);
    }

    /// <summary>
    /// Render aim point.
    /// </summary>
    private void RenderAimPoint(Graphics g, IntelligentDetection detection, Color color)
    {
        var aimPoint = detection.GetAimPoint(true);
        float size = 6;

        // Crosshair
        using var pen = new Pen(Color.FromArgb(200, 255, 255, 0), 1);
        g.DrawLine(pen, aimPoint.X - size, aimPoint.Y, aimPoint.X + size, aimPoint.Y);
        g.DrawLine(pen, aimPoint.X, aimPoint.Y - size, aimPoint.X, aimPoint.Y + size);

        // Center dot
        using var brush = new SolidBrush(Color.FromArgb(200, 255, 50, 50));
        g.FillEllipse(brush, aimPoint.X - 2, aimPoint.Y - 2, 4, 4);
    }

    /// <summary>
    /// Render frame info panel.
    /// </summary>
    private void RenderFrameInfo(Graphics g, IntelligenceFrame frame, int width, int height)
    {
        int panelX = 10;
        int panelY = 10;
        int panelWidth = 250;
        int lineHeight = 18;
        int padding = 8;

        var lines = new List<(string text, Color color)>
        {
            ("INTELLIGENCE", Color.Cyan),
            ("─────────────────────", Color.Gray),
            ($"Detections: {frame.DetectionCount}", Color.White),
            ($"Threats: {frame.ThreatCount}", frame.ThreatCount > 0 ? Color.Red : Color.Green),
            ($"Opportunities: {frame.OpportunityCount}", frame.OpportunityCount > 0 ? Color.LimeGreen : Color.Gray),
            ("─────────────────────", Color.Gray),
            ($"Threat Level: {frame.ThreatLevel:P0}", GetThreatColor(frame.ThreatLevel)),
            ($"Opportunity Level: {frame.OpportunityLevel:P0}", Color.LimeGreen),
            ($"Confidence: {frame.Confidence:P0}", GetConfidenceColor(frame.Confidence)),
            ("─────────────────────", Color.Gray),
            ($"Action: {frame.RecommendedAction.Type}", Color.Yellow),
            ($"  └─ {frame.RecommendedAction.Reason}", Color.LightGray)
        };

        int panelHeight = lines.Count * lineHeight + padding * 2;

        // Background
        using var bgBrush = new SolidBrush(Color.FromArgb(200, 20, 20, 20));
        using var borderPen = new Pen(Color.FromArgb(200, 60, 60, 60), 1);
        g.FillRectangle(bgBrush, panelX, panelY, panelWidth, panelHeight);
        g.DrawRectangle(borderPen, panelX, panelY, panelWidth, panelHeight);

        // Lines
        int y = panelY + padding;
        foreach (var (text, color) in lines)
        {
            using var brush = new SolidBrush(color);
            g.DrawString(text, _smallFont!, brush, panelX + padding, y);
            y += lineHeight;
        }
    }

    private static Color GetTagColor(string category)
    {
        return category.ToLower() switch
        {
            "threat" => Color.FromArgb(150, 50, 50),
            "opportunity" => Color.FromArgb(50, 150, 50),
            "behavior" => Color.FromArgb(150, 100, 50),
            "learned" => Color.FromArgb(100, 50, 150),
            _ => Color.FromArgb(80, 80, 80)
        };
    }

    private static Color GetThreatColor(float level)
    {
        if (level > 0.7f) return Color.Red;
        if (level > 0.4f) return Color.Orange;
        if (level > 0.1f) return Color.Yellow;
        return Color.Green;
    }

    private static Color GetConfidenceColor(float confidence)
    {
        if (confidence >= 0.8f) return Color.LimeGreen;
        if (confidence >= 0.5f) return Color.Yellow;
        if (confidence >= 0.3f) return Color.Orange;
        return Color.Red;
    }
}

/// <summary>
/// Render style configuration.
/// </summary>
public sealed class RenderStyle
{
    // Fonts
    public string FontFamily { get; init; } = "Consolas";
    public float LabelFontSize { get; init; } = 10f;
    public float TagFontSize { get; init; } = 8f;
    public float SmallFontSize { get; init; } = 9f;

    // Box style
    public BoxDrawStyle BoxStyle { get; init; } = BoxDrawStyle.Corner;
    public float BoxThickness { get; init; } = 2f;
    public float CornerThickness { get; init; } = 3f;
    public float CornerRatio { get; init; } = 0.2f;

    // Label
    public int LabelBackgroundAlpha { get; init; } = 180;
    public bool ShowTrackId { get; init; } = true;
    public bool ShowConfidenceInLabel { get; init; } = true;

    // Features
    public bool ShowConfidenceBar { get; init; } = true;
    public bool ShowVelocity { get; init; } = true;
    public bool ShowIndicators { get; init; } = true;
    public bool ShowTags { get; init; } = true;
    public bool ShowTrustBadge { get; init; } = true;
    public bool ShowAimPoint { get; init; } = true;

    // Effects
    public bool EnableGlow { get; init; } = true;
    public int GlowSize { get; init; } = 8;
    public float VelocityScale { get; init; } = 20f;

    public static RenderStyle Default => new();

    public static RenderStyle Minimal => new()
    {
        BoxStyle = BoxDrawStyle.Solid,
        ShowConfidenceBar = false,
        ShowTags = false,
        ShowIndicators = false,
        EnableGlow = false
    };

    public static RenderStyle HighDetail => new()
    {
        ShowConfidenceBar = true,
        ShowVelocity = true,
        ShowIndicators = true,
        ShowTags = true,
        ShowTrustBadge = true,
        ShowAimPoint = true,
        EnableGlow = true,
        GlowSize = 12
    };
}

/// <summary>
/// Box drawing style.
/// </summary>
public enum BoxDrawStyle
{
    Solid,
    Corner
}
