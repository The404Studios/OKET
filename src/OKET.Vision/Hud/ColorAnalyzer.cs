using OKET.Core.Types;

namespace OKET.Vision.Hud;

/// <summary>
/// Analyzes colors in frame regions for HUD element detection.
/// </summary>
public static class ColorAnalyzer
{
    /// <summary>
    /// Calculate the average color of a region.
    /// </summary>
    public static (float R, float G, float B) AverageColor(Frame frame, BoundingBox region)
    {
        int x1 = Math.Max(0, (int)region.X);
        int y1 = Math.Max(0, (int)region.Y);
        int x2 = Math.Min(frame.Width - 1, (int)region.Right);
        int y2 = Math.Min(frame.Height - 1, (int)region.Bottom);

        long totalR = 0, totalG = 0, totalB = 0;
        int count = 0;

        for (int y = y1; y <= y2; y++)
        {
            for (int x = x1; x <= x2; x++)
            {
                var (b, g, r, _) = frame.GetPixel(x, y);
                totalR += r;
                totalG += g;
                totalB += b;
                count++;
            }
        }

        if (count == 0) return (0, 0, 0);

        return ((float)totalR / count, (float)totalG / count, (float)totalB / count);
    }

    /// <summary>
    /// Count pixels matching a color condition.
    /// </summary>
    public static int CountPixelsMatching(Frame frame, BoundingBox region, Func<byte, byte, byte, bool> condition)
    {
        int x1 = Math.Max(0, (int)region.X);
        int y1 = Math.Max(0, (int)region.Y);
        int x2 = Math.Min(frame.Width - 1, (int)region.Right);
        int y2 = Math.Min(frame.Height - 1, (int)region.Bottom);

        int count = 0;

        for (int y = y1; y <= y2; y++)
        {
            for (int x = x1; x <= x2; x++)
            {
                var (b, g, r, _) = frame.GetPixel(x, y);
                if (condition(r, g, b))
                    count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Measure a horizontal bar's fill percentage by detecting color change.
    /// </summary>
    public static float MeasureHorizontalBar(Frame frame, BoundingBox region,
        Func<byte, byte, byte, bool> isFilled)
    {
        int x1 = (int)region.X;
        int x2 = (int)region.Right;
        int y = (int)(region.Y + region.Height / 2);

        int filledCount = 0;
        int totalCount = 0;

        for (int x = x1; x < x2; x++)
        {
            var (b, g, r, _) = frame.GetPixel(x, y);
            if (isFilled(r, g, b))
                filledCount++;
            totalCount++;
        }

        return totalCount > 0 ? (float)filledCount / totalCount : 0;
    }

    /// <summary>
    /// Check if a region is predominantly a specific color (for death screen detection).
    /// </summary>
    public static bool IsRegionColor(Frame frame, BoundingBox region,
        byte targetR, byte targetG, byte targetB, float tolerance = 30f, float threshold = 0.5f)
    {
        int totalPixels = (int)(region.Width * region.Height);
        int matchingPixels = CountPixelsMatching(frame, region, (r, g, b) =>
        {
            float dr = r - targetR;
            float dg = g - targetG;
            float db = b - targetB;
            return Math.Sqrt(dr * dr + dg * dg + db * db) < tolerance;
        });

        return (float)matchingPixels / totalPixels >= threshold;
    }

    /// <summary>
    /// Check if region has significant red tint (damage/death indicator).
    /// </summary>
    public static bool HasRedTint(Frame frame, BoundingBox region, float threshold = 0.3f)
    {
        var (r, g, b) = AverageColor(frame, region);
        float redDominance = r / Math.Max(1, (r + g + b));
        return redDominance > threshold && r > 100;
    }

    /// <summary>
    /// Detect hit marker (typically a white X or crosshair flash).
    /// </summary>
    public static bool DetectHitMarker(Frame frame, BoundingBox crosshairRegion)
    {
        // Hit markers are typically bright white near the crosshair
        int brightPixels = CountPixelsMatching(frame, crosshairRegion, (r, g, b) =>
            r > 220 && g > 220 && b > 220);

        int totalPixels = (int)(crosshairRegion.Width * crosshairRegion.Height);
        float brightRatio = (float)brightPixels / totalPixels;

        // Hit markers cause a spike in bright pixels
        return brightRatio > 0.05f && brightRatio < 0.3f;
    }
}
