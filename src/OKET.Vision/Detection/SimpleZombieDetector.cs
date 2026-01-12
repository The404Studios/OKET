using OKET.Core.Types;
using OKET.Core.Interfaces;
using CoreDetection = OKET.Core.Detection;

namespace OKET.Vision.Detection;

/// <summary>
/// Simple color/shape-based zombie detector for bootstrapping.
/// Works without a trained ML model by using heuristics.
/// </summary>
public sealed class SimpleZombieDetector : IObjectDetector
{
    public bool IsReady => true;
    public float ConfidenceThreshold { get; set; } = 0.3f;

    public IReadOnlyList<CoreDetection.DetectionClass> SupportedClasses { get; } = new[]
    {
        CoreDetection.DetectionClass.Zombie,
        CoreDetection.DetectionClass.Headcrab
    };

    // Colors commonly associated with zombies in GMod ZS
    private static readonly (byte R, byte G, byte B)[] ZombieColors =
    [
        ((byte)100, (byte)80, (byte)60),   // Brown/tan skin
        ((byte)80, (byte)100, (byte)80),   // Greenish zombie
        ((byte)60, (byte)60, (byte)80),    // Grayish
        ((byte)120, (byte)80, (byte)80),   // Reddish
    ];

    public Task LoadAsync(string modelPath, CancellationToken ct = default)
    {
        // No model to load for heuristic detector
        return Task.CompletedTask;
    }

    public Task<CoreDetection.DetectionResult> DetectAsync(Frame frame, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var detections = new List<CoreDetection.Detection>();

        // Scan for zombie-colored regions
        // This is a simplified blob detection approach
        int gridSize = 40; // Sample every 40 pixels
        var candidates = new List<(int x, int y, float score)>();

        for (int y = gridSize; y < frame.Height - gridSize; y += gridSize)
        {
            for (int x = gridSize; x < frame.Width - gridSize; x += gridSize)
            {
                float score = CalculateZombieScore(frame, x, y, gridSize);
                if (score > ConfidenceThreshold)
                {
                    candidates.Add((x, y, score));
                }
            }
        }

        // Cluster nearby candidates into detections
        var clusters = ClusterCandidates(candidates, gridSize * 2);

        foreach (var cluster in clusters)
        {
            if (cluster.Points.Count < 2) continue;

            var box = GetBoundingBox(cluster.Points, gridSize);
            float confidence = cluster.Points.Average(p => p.score);

            // Estimate size for classification
            bool isSmall = box.Area < 5000;

            detections.Add(new CoreDetection.Detection
            {
                Class = isSmall ? CoreDetection.DetectionClass.Headcrab : CoreDetection.DetectionClass.Zombie,
                Confidence = Math.Min(confidence, 0.6f), // Cap confidence for heuristic detector
                Box = box,
                FrameId = frame.Id,
                Priority = confidence * (isSmall ? 0.8f : 1.0f)
            });
        }

        sw.Stop();

        return Task.FromResult(new CoreDetection.DetectionResult
        {
            FrameId = frame.Id,
            InferenceTimeMs = sw.ElapsedMilliseconds,
            Detections = detections
        });
    }

    private static float CalculateZombieScore(Frame frame, int cx, int cy, int radius)
    {
        float totalScore = 0;
        int samples = 0;

        // Sample pixels in a small region
        for (int dy = -radius / 2; dy <= radius / 2; dy += 4)
        {
            for (int dx = -radius / 2; dx <= radius / 2; dx += 4)
            {
                int px = cx + dx;
                int py = cy + dy;

                if (px < 0 || px >= frame.Width || py < 0 || py >= frame.Height)
                    continue;

                var (b, g, r, _) = frame.GetPixel(px, py);

                // Calculate similarity to zombie colors
                float bestMatch = 0;
                foreach (var zombieColor in ZombieColors)
                {
                    float dr = (r - zombieColor.R) / 255f;
                    float dg = (g - zombieColor.G) / 255f;
                    float db = (b - zombieColor.B) / 255f;
                    float dist = MathF.Sqrt(dr * dr + dg * dg + db * db);
                    float match = 1f - Math.Min(dist, 1f);
                    bestMatch = Math.Max(bestMatch, match);
                }

                // Also check for "unusual" colors that might be zombies
                // (not too bright, not sky blue, not pure green like grass)
                bool isNotSky = b < 200 || g < 200 || r > 100;
                bool isNotGrass = g < 200 || r > 50 || b > 50;
                bool isNotTooBase = (r + g + b) > 100 && (r + g + b) < 500;

                if (isNotSky && isNotGrass && isNotTooBase)
                {
                    totalScore += bestMatch * 0.7f + 0.3f;
                }

                samples++;
            }
        }

        return samples > 0 ? totalScore / samples : 0;
    }

    private record Cluster(List<(int x, int y, float score)> Points);

    private static List<Cluster> ClusterCandidates(List<(int x, int y, float score)> candidates, int maxDist)
    {
        var clusters = new List<Cluster>();
        var used = new HashSet<int>();

        for (int i = 0; i < candidates.Count; i++)
        {
            if (used.Contains(i)) continue;

            var cluster = new Cluster(new List<(int x, int y, float score)> { candidates[i] });
            used.Add(i);

            // Find all nearby points
            bool added = true;
            while (added)
            {
                added = false;
                for (int j = 0; j < candidates.Count; j++)
                {
                    if (used.Contains(j)) continue;

                    foreach (var point in cluster.Points)
                    {
                        int dx = candidates[j].x - point.x;
                        int dy = candidates[j].y - point.y;
                        if (dx * dx + dy * dy < maxDist * maxDist)
                        {
                            cluster.Points.Add(candidates[j]);
                            used.Add(j);
                            added = true;
                            break;
                        }
                    }
                }
            }

            clusters.Add(cluster);
        }

        return clusters;
    }

    private static BoundingBox GetBoundingBox(List<(int x, int y, float score)> points, int padding)
    {
        int minX = points.Min(p => p.x) - padding;
        int maxX = points.Max(p => p.x) + padding;
        int minY = points.Min(p => p.y) - padding;
        int maxY = points.Max(p => p.y) + padding;

        return new BoundingBox(minX, minY, maxX - minX, maxY - minY);
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}
