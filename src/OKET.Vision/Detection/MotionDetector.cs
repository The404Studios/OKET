using OKET.Core.Types;
using OKET.Core.Interfaces;
using CoreDetection = OKET.Core.Detection;

namespace OKET.Vision.Detection;

/// <summary>
/// Motion-based object detector using frame differencing.
/// Detects any moving objects regardless of color by comparing consecutive frames.
/// Also detects player-like silhouettes using shape analysis.
/// </summary>
public sealed class MotionDetector : IObjectDetector
{
    private Frame? _previousFrame;
    private readonly byte[] _motionBuffer;
    private readonly int _maxWidth = 1920;
    private readonly int _maxHeight = 1080;
    private int _trackIdCounter;

    public bool IsReady => true;
    public float ConfidenceThreshold { get; set; } = 0.3f;

    /// <summary>Minimum pixel difference to count as motion.</summary>
    public int MotionThreshold { get; set; } = 30;

    /// <summary>Minimum area for a detection (pixels).</summary>
    public int MinArea { get; set; } = 500;

    /// <summary>Maximum area for a detection (pixels).</summary>
    public int MaxArea { get; set; } = 200000;

    public IReadOnlyList<CoreDetection.DetectionClass> SupportedClasses { get; } = new[]
    {
        CoreDetection.DetectionClass.Player,
        CoreDetection.DetectionClass.Zombie,
        CoreDetection.DetectionClass.Unknown
    };

    public MotionDetector()
    {
        _motionBuffer = new byte[_maxWidth * _maxHeight];
    }

    public Task LoadAsync(string modelPath, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task<CoreDetection.DetectionResult> DetectAsync(Frame frame, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var detections = new List<CoreDetection.Detection>();

        if (_previousFrame != null && _previousFrame.Width == frame.Width && _previousFrame.Height == frame.Height)
        {
            // Compute motion mask
            ComputeMotionMask(frame, _previousFrame);

            // Find connected components (moving regions)
            var regions = FindMotionRegions(frame.Width, frame.Height);

            foreach (var region in regions)
            {
                // Skip regions that are too small or too large
                if (region.Area < MinArea || region.Area > MaxArea)
                    continue;

                var box = new BoundingBox(region.MinX, region.MinY,
                    region.MaxX - region.MinX, region.MaxY - region.MinY);

                // Classify based on shape and size
                var (detClass, confidence) = ClassifyRegion(frame, region, box);

                if (confidence >= ConfidenceThreshold)
                {
                    detections.Add(new CoreDetection.Detection
                    {
                        TrackId = ++_trackIdCounter,
                        Class = detClass,
                        Confidence = confidence,
                        Box = box,
                        FrameId = frame.Id,
                        Priority = confidence * (detClass == CoreDetection.DetectionClass.Player ? 1.0f : 0.8f)
                    });
                }
            }
        }

        // Also detect player-like shapes using skin/clothing colors
        var playerDetections = DetectPlayerShapes(frame);
        foreach (var det in playerDetections)
        {
            // Avoid duplicates with motion detections
            bool overlaps = detections.Any(d =>
                CalculateIoU(d.Box, det.Box) > 0.5f);

            if (!overlaps)
            {
                detections.Add(det);
            }
        }

        // Store for next frame
        _previousFrame?.Dispose();
        _previousFrame = frame.Clone();

        sw.Stop();

        return Task.FromResult(new CoreDetection.DetectionResult
        {
            FrameId = frame.Id,
            InferenceTimeMs = sw.ElapsedMilliseconds,
            Detections = detections
        });
    }

    private void ComputeMotionMask(Frame current, Frame previous)
    {
        Array.Clear(_motionBuffer);

        int stride = current.Width;
        var currentData = current.RawData;
        var prevData = previous.RawData;

        // Process in parallel for speed
        Parallel.For(0, current.Height, y =>
        {
            int rowOffset = y * stride * 4;
            for (int x = 0; x < current.Width; x++)
            {
                int i = rowOffset + x * 4;

                if (i + 2 >= currentData.Length || i + 2 >= prevData.Length)
                    continue;

                // Calculate absolute difference for each channel
                int diffB = Math.Abs(currentData[i] - prevData[i]);
                int diffG = Math.Abs(currentData[i + 1] - prevData[i + 1]);
                int diffR = Math.Abs(currentData[i + 2] - prevData[i + 2]);

                // Motion detected if any channel differs significantly
                int maxDiff = Math.Max(Math.Max(diffB, diffG), diffR);

                _motionBuffer[y * stride + x] = maxDiff > MotionThreshold ? (byte)255 : (byte)0;
            }
        });
    }

    private List<MotionRegion> FindMotionRegions(int width, int height)
    {
        var regions = new List<MotionRegion>();
        var visited = new bool[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                if (_motionBuffer[idx] == 0 || visited[idx])
                    continue;

                // Flood fill to find connected region
                var region = new MotionRegion();
                var stack = new Stack<(int x, int y)>();
                stack.Push((x, y));

                while (stack.Count > 0)
                {
                    var (cx, cy) = stack.Pop();
                    int cidx = cy * width + cx;

                    if (cx < 0 || cx >= width || cy < 0 || cy >= height)
                        continue;
                    if (visited[cidx] || _motionBuffer[cidx] == 0)
                        continue;

                    visited[cidx] = true;
                    region.MinX = Math.Min(region.MinX, cx);
                    region.MaxX = Math.Max(region.MaxX, cx);
                    region.MinY = Math.Min(region.MinY, cy);
                    region.MaxY = Math.Max(region.MaxY, cy);
                    region.PixelCount++;

                    // Add neighbors (4-connected)
                    stack.Push((cx - 1, cy));
                    stack.Push((cx + 1, cy));
                    stack.Push((cx, cy - 1));
                    stack.Push((cx, cy + 1));
                }

                if (region.PixelCount > 50) // Minimum pixels
                {
                    regions.Add(region);
                }
            }
        }

        return regions;
    }

    private (CoreDetection.DetectionClass, float) ClassifyRegion(Frame frame, MotionRegion region, BoundingBox box)
    {
        // Analyze aspect ratio
        float aspectRatio = box.Width / (float)Math.Max(1, box.Height);

        // Players are typically taller than wide (aspect ratio < 1)
        // Zombies similar but may be crouching
        // Headcrabs are wider and small

        float playerScore = 0f;
        float zombieScore = 0f;

        // Aspect ratio analysis
        if (aspectRatio < 0.7f) // Tall and thin = player/zombie standing
        {
            playerScore += 0.4f;
            zombieScore += 0.3f;
        }
        else if (aspectRatio < 1.2f) // Roughly square = crouching or small
        {
            playerScore += 0.2f;
            zombieScore += 0.3f;
        }

        // Size analysis
        float sizeRatio = box.Area / (float)(frame.Width * frame.Height);
        if (sizeRatio > 0.01f && sizeRatio < 0.15f) // Reasonable player size
        {
            playerScore += 0.3f;
        }

        // Sample colors in the region to look for skin tones or player colors
        int skinPixels = 0;
        int samples = 0;

        for (int sy = region.MinY; sy < region.MaxY; sy += 5)
        {
            for (int sx = region.MinX; sx < region.MaxX; sx += 5)
            {
                if (sx < 0 || sx >= frame.Width || sy < 0 || sy >= frame.Height)
                    continue;

                var (b, g, r, _) = frame.GetPixel(sx, sy);

                // Check for skin-like colors
                if (IsSkinColor(r, g, b))
                {
                    skinPixels++;
                }

                // Check for clothing colors (varied, non-sky, non-grass)
                if (IsClothingColor(r, g, b))
                {
                    playerScore += 0.01f;
                }

                samples++;
            }
        }

        if (samples > 0)
        {
            float skinRatio = skinPixels / (float)samples;
            if (skinRatio > 0.05f && skinRatio < 0.4f) // Some skin visible = player
            {
                playerScore += 0.3f;
            }
        }

        // Determine class
        float maxScore = Math.Max(playerScore, zombieScore);
        if (maxScore < 0.3f)
        {
            return (CoreDetection.DetectionClass.Unknown, maxScore + 0.3f);
        }

        if (playerScore > zombieScore)
        {
            return (CoreDetection.DetectionClass.Player, Math.Min(playerScore, 0.85f));
        }
        else
        {
            return (CoreDetection.DetectionClass.Zombie, Math.Min(zombieScore, 0.75f));
        }
    }

    private List<CoreDetection.Detection> DetectPlayerShapes(Frame frame)
    {
        var detections = new List<CoreDetection.Detection>();

        // Scan for player-colored regions (skin + clothing)
        int gridSize = 30;
        var candidates = new List<(int x, int y, float score)>();

        for (int y = gridSize; y < frame.Height - gridSize; y += gridSize)
        {
            for (int x = gridSize; x < frame.Width - gridSize; x += gridSize)
            {
                float score = CalculatePlayerScore(frame, x, y, gridSize);
                if (score > 0.4f)
                {
                    candidates.Add((x, y, score));
                }
            }
        }

        // Cluster candidates
        var clusters = ClusterCandidates(candidates, gridSize * 2);

        foreach (var cluster in clusters)
        {
            if (cluster.Count < 2) continue;

            int minX = cluster.Min(p => p.x) - gridSize;
            int maxX = cluster.Max(p => p.x) + gridSize;
            int minY = cluster.Min(p => p.y) - gridSize;
            int maxY = cluster.Max(p => p.y) + gridSize;

            var box = new BoundingBox(
                Math.Max(0, minX),
                Math.Max(0, minY),
                Math.Min(frame.Width - minX, maxX - minX),
                Math.Min(frame.Height - minY, maxY - minY));

            if (box.Area < MinArea || box.Area > MaxArea)
                continue;

            float confidence = cluster.Average(p => p.score);

            detections.Add(new CoreDetection.Detection
            {
                TrackId = ++_trackIdCounter,
                Class = CoreDetection.DetectionClass.Player,
                Confidence = Math.Min(confidence, 0.7f),
                Box = box,
                FrameId = frame.Id,
                Priority = confidence
            });
        }

        return detections;
    }

    private static float CalculatePlayerScore(Frame frame, int cx, int cy, int radius)
    {
        float skinScore = 0;
        float clothingScore = 0;
        int samples = 0;

        for (int dy = -radius / 2; dy <= radius / 2; dy += 3)
        {
            for (int dx = -radius / 2; dx <= radius / 2; dx += 3)
            {
                int px = cx + dx;
                int py = cy + dy;

                if (px < 0 || px >= frame.Width || py < 0 || py >= frame.Height)
                    continue;

                var (b, g, r, _) = frame.GetPixel(px, py);

                if (IsSkinColor(r, g, b)) skinScore += 1;
                if (IsClothingColor(r, g, b)) clothingScore += 0.5f;

                samples++;
            }
        }

        if (samples == 0) return 0;

        // Player detection: needs both skin and clothing
        float skinRatio = skinScore / samples;
        float clothingRatio = clothingScore / samples;

        // Sweet spot: some skin, some clothing, not all one thing
        if (skinRatio > 0.05f && skinRatio < 0.5f && clothingRatio > 0.1f)
        {
            return (skinRatio * 2 + clothingRatio) / 3f + 0.3f;
        }

        return clothingRatio * 0.5f;
    }

    private static bool IsSkinColor(byte r, byte g, byte b)
    {
        // Skin tone detection using YCbCr-like approach
        // Skin tones have specific relationships between RGB
        if (r < 60 || g < 40 || b < 20) return false;
        if (r < g || r < b) return false; // Red should be dominant

        float rg = r - g;
        float rb = r - b;

        // Skin typically has R > G > B with specific ratios
        return rg > 10 && rg < 100 && rb > 20 && rb < 150;
    }

    private static bool IsClothingColor(byte r, byte g, byte b)
    {
        // Clothing: not sky (high blue), not grass (high green), not too dark/bright
        int brightness = (r + g + b) / 3;

        bool notSky = b < 200 || g < 180 || r > 80;
        bool notGrass = g < 180 || r > 60 || b > 60;
        bool notExtreme = brightness > 30 && brightness < 220;

        return notSky && notGrass && notExtreme;
    }

    private static List<List<(int x, int y, float score)>> ClusterCandidates(
        List<(int x, int y, float score)> candidates, int maxDist)
    {
        var clusters = new List<List<(int x, int y, float score)>>();
        var used = new HashSet<int>();

        for (int i = 0; i < candidates.Count; i++)
        {
            if (used.Contains(i)) continue;

            var cluster = new List<(int x, int y, float score)> { candidates[i] };
            used.Add(i);

            bool added = true;
            while (added)
            {
                added = false;
                for (int j = 0; j < candidates.Count; j++)
                {
                    if (used.Contains(j)) continue;

                    foreach (var point in cluster)
                    {
                        int dx = candidates[j].x - point.x;
                        int dy = candidates[j].y - point.y;
                        if (dx * dx + dy * dy < maxDist * maxDist)
                        {
                            cluster.Add(candidates[j]);
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

    private static float CalculateIoU(BoundingBox a, BoundingBox b)
    {
        int x1 = Math.Max(a.X, b.X);
        int y1 = Math.Max(a.Y, b.Y);
        int x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        int y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

        if (x2 <= x1 || y2 <= y1) return 0;

        float intersection = (x2 - x1) * (y2 - y1);
        float union = a.Area + b.Area - intersection;

        return intersection / union;
    }

    public void Dispose()
    {
        _previousFrame?.Dispose();
    }

    private class MotionRegion
    {
        public int MinX = int.MaxValue;
        public int MaxX = int.MinValue;
        public int MinY = int.MaxValue;
        public int MaxY = int.MinValue;
        public int PixelCount;

        public int Area => (MaxX - MinX) * (MaxY - MinY);
    }
}
