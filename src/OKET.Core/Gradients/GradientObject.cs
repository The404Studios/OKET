namespace OKET.Core.Gradients;

/// <summary>
/// Regional Layer: Gradient Object.
///
/// PRINCIPLE: Objects are fields that cohere.
/// A Gradient Object is a region where signals are internally consistent
/// across space AND time.
///
/// Created by clustering cells using combined similarity:
/// - Similar motion (flow direction + speed)
/// - Similar color signature (HSV)
/// - Coherent edges/contours
/// - Temporal persistence
///
/// This is NOT "enemy" or "item" - it's "coherent moving red-ish vertical blob."
/// Names come later, only after stabilization.
/// </summary>
public sealed class GradientObject
{
    private readonly List<(int gx, int gy)> _cells = new();
    private readonly int _objectId;

    // Spatial properties
    private float _centroidX;
    private float _centroidY;
    private int _minX, _maxX, _minY, _maxY;

    // Motion properties
    private float _velocityX;
    private float _velocityY;
    private float _accelerationX;
    private float _accelerationY;
    private float _prevVelocityX;
    private float _prevVelocityY;

    // Shape properties
    private float _area;
    private float _aspectRatio;
    private float _compactness;
    private float _edgeDensity;
    private float _contourComplexity;

    // Color properties
    private float _dominantHue;
    private float _hueVariance;
    private float _meanSaturation;
    private float _meanValue;
    private readonly float[] _hueHistogram = new float[12]; // 30° bins

    // Temporal properties
    private int _ageFrames;
    private float _jitter; // Position variance over time
    private float _occlusionRate;
    private float _stability;
    private long _createdFrame;
    private long _lastSeenFrame;

    // Tracking
    private float _confidence;
    private bool _isTracked;
    private readonly Queue<(float x, float y)> _positionHistory = new();
    private const int MaxPositionHistory = 30;

    // Identity (provisional until stable)
    private int _prototypeId = -1;
    private float _prototypeMatch;
    private string? _stableName;

    public int ObjectId => _objectId;
    public IReadOnlyList<(int gx, int gy)> Cells => _cells;
    public float CentroidX => _centroidX;
    public float CentroidY => _centroidY;
    public float VelocityX => _velocityX;
    public float VelocityY => _velocityY;
    public float Speed => MathF.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);
    public float Area => _area;
    public float AspectRatio => _aspectRatio;
    public float Compactness => _compactness;
    public float EdgeDensity => _edgeDensity;
    public float DominantHue => _dominantHue;
    public float MeanSaturation => _meanSaturation;
    public float MeanValue => _meanValue;
    public int AgeFrames => _ageFrames;
    public float Stability => _stability;
    public float Confidence => _confidence;
    public bool IsTracked => _isTracked;
    public int PrototypeId => _prototypeId;
    public float PrototypeMatch => _prototypeMatch;
    public string? StableName => _stableName;
    public bool IsStable => _stability > 0.7f && _ageFrames > 30;
    public bool HasIdentity => _prototypeId >= 0;

    /// <summary>Bounding box.</summary>
    public (int minX, int minY, int maxX, int maxY) BoundingBox => (_minX, _minY, _maxX, _maxY);

    public GradientObject(int objectId, long frameId)
    {
        _objectId = objectId;
        _createdFrame = frameId;
        _lastSeenFrame = frameId;
        _confidence = 0.5f;
    }

    /// <summary>
    /// Add a cell to this gradient object.
    /// </summary>
    public void AddCell(int gx, int gy)
    {
        _cells.Add((gx, gy));
    }

    /// <summary>
    /// Compute all properties from the current cells and field data.
    /// </summary>
    public void ComputeProperties(GradientField field, long frameId)
    {
        if (_cells.Count == 0)
        {
            _confidence = 0;
            return;
        }

        _lastSeenFrame = frameId;
        _ageFrames = (int)(frameId - _createdFrame);

        // Store previous velocity for acceleration
        _prevVelocityX = _velocityX;
        _prevVelocityY = _velocityY;

        ComputeSpatialProperties(field);
        ComputeMotionProperties(field);
        ComputeShapeProperties(field);
        ComputeColorProperties(field);
        ComputeTemporalProperties();
        ComputeConfidence();
    }

    private void ComputeSpatialProperties(GradientField field)
    {
        if (_cells.Count == 0) return;

        float sumX = 0, sumY = 0;
        _minX = int.MaxValue;
        _maxX = int.MinValue;
        _minY = int.MaxValue;
        _maxY = int.MinValue;

        foreach (var (gx, gy) in _cells)
        {
            sumX += gx;
            sumY += gy;
            _minX = Math.Min(_minX, gx);
            _maxX = Math.Max(_maxX, gx);
            _minY = Math.Min(_minY, gy);
            _maxY = Math.Max(_maxY, gy);
        }

        float prevCentroidX = _centroidX;
        float prevCentroidY = _centroidY;

        _centroidX = sumX / _cells.Count;
        _centroidY = sumY / _cells.Count;
        _area = _cells.Count;

        // Update position history for jitter calculation
        _positionHistory.Enqueue((_centroidX, _centroidY));
        while (_positionHistory.Count > MaxPositionHistory)
            _positionHistory.Dequeue();
    }

    private void ComputeMotionProperties(GradientField field)
    {
        if (_cells.Count == 0) return;

        float sumFx = 0, sumFy = 0;
        foreach (var (gx, gy) in _cells)
        {
            var sample = field.GetSample(gx, gy);
            sumFx += sample.FlowX;
            sumFy += sample.FlowY;
        }

        _velocityX = sumFx / _cells.Count;
        _velocityY = sumFy / _cells.Count;

        // Acceleration
        _accelerationX = _velocityX - _prevVelocityX;
        _accelerationY = _velocityY - _prevVelocityY;
    }

    private void ComputeShapeProperties(GradientField field)
    {
        // Aspect ratio
        int width = _maxX - _minX + 1;
        int height = _maxY - _minY + 1;
        _aspectRatio = width > 0 && height > 0 ? (float)height / width : 1f;

        // Compactness (how close to a filled rectangle)
        float boundingArea = width * height;
        _compactness = boundingArea > 0 ? _area / boundingArea : 0;

        // Edge density
        float edgeSum = 0;
        int edgeCount = 0;
        foreach (var (gx, gy) in _cells)
        {
            // Count cells on the border
            bool isBorder = !_cells.Contains((gx - 1, gy)) ||
                           !_cells.Contains((gx + 1, gy)) ||
                           !_cells.Contains((gx, gy - 1)) ||
                           !_cells.Contains((gx, gy + 1));
            if (isBorder)
            {
                edgeSum += field.GetSample(gx, gy).EdgeMagnitude;
                edgeCount++;
            }
        }
        _edgeDensity = edgeCount > 0 ? edgeSum / edgeCount : 0;

        // Contour complexity (perimeter / sqrt(area))
        float perimeter = edgeCount;
        _contourComplexity = _area > 0 ? perimeter / MathF.Sqrt(_area) : 0;
    }

    private void ComputeColorProperties(GradientField field)
    {
        if (_cells.Count == 0) return;

        // Reset histogram
        Array.Clear(_hueHistogram);

        float sumH = 0, sumS = 0, sumV = 0;
        float sumHSq = 0;

        foreach (var (gx, gy) in _cells)
        {
            var sample = field.GetSample(gx, gy);

            sumH += sample.Hue;
            sumS += sample.Saturation;
            sumV += sample.Value;
            sumHSq += sample.Hue * sample.Hue;

            // Histogram bin (12 bins of 30° each)
            int bin = Math.Clamp((int)(sample.Hue * 12), 0, 11);
            _hueHistogram[bin] += 1;
        }

        float count = _cells.Count;
        float meanH = sumH / count;
        _meanSaturation = sumS / count;
        _meanValue = sumV / count;
        _hueVariance = (sumHSq / count) - (meanH * meanH);

        // Find dominant hue from histogram
        int maxBin = 0;
        float maxCount = 0;
        for (int i = 0; i < 12; i++)
        {
            if (_hueHistogram[i] > maxCount)
            {
                maxCount = _hueHistogram[i];
                maxBin = i;
            }
        }
        _dominantHue = (maxBin + 0.5f) / 12f; // Center of bin
    }

    private void ComputeTemporalProperties()
    {
        // Jitter = variance in position over time
        if (_positionHistory.Count >= 5)
        {
            var positions = _positionHistory.ToArray();
            float meanX = positions.Average(p => p.x);
            float meanY = positions.Average(p => p.y);
            float variance = positions.Average(p =>
                (p.x - meanX) * (p.x - meanX) + (p.y - meanY) * (p.y - meanY));
            _jitter = MathF.Sqrt(variance);
        }

        // Stability increases with age and low jitter
        float ageFactor = Math.Min(1f, _ageFrames / 60f);
        float jitterFactor = 1f / (1f + _jitter);
        float sizeFactor = Math.Min(1f, _area / 10f);

        _stability = ageFactor * 0.4f + jitterFactor * 0.3f + sizeFactor * 0.3f;
    }

    private void ComputeConfidence()
    {
        // Confidence based on multiple factors
        float sizeFactor = Math.Min(1f, _area / 5f);
        float stabilityFactor = _stability;
        float edgeFactor = Math.Min(1f, _edgeDensity * 2f);
        float trackingFactor = _isTracked ? 1f : 0.5f;

        _confidence = (sizeFactor * 0.2f + stabilityFactor * 0.3f +
                      edgeFactor * 0.2f + trackingFactor * 0.3f);
    }

    /// <summary>
    /// Get the signature vector for this object.
    /// This is the "token" representation used for matching.
    /// </summary>
    public SignatureVector GetSignature()
    {
        return new SignatureVector
        {
            // Motion (4)
            VelocityX = _velocityX,
            VelocityY = _velocityY,
            Speed = Speed,
            Acceleration = MathF.Sqrt(_accelerationX * _accelerationX + _accelerationY * _accelerationY),

            // Shape (4)
            Area = _area,
            AspectRatio = _aspectRatio,
            Compactness = _compactness,
            EdgeDensity = _edgeDensity,

            // Color (4)
            DominantHue = _dominantHue,
            HueVariance = _hueVariance,
            Saturation = _meanSaturation,
            Value = _meanValue,

            // Temporal (3)
            AgeFrames = _ageFrames,
            Jitter = _jitter,
            Stability = _stability,

            // Context (3)
            NormalizedX = _centroidX,
            NormalizedY = _centroidY,
            Confidence = _confidence
        };
    }

    /// <summary>
    /// Set prototype match from library lookup.
    /// </summary>
    public void SetPrototype(int prototypeId, float matchScore)
    {
        _prototypeId = prototypeId;
        _prototypeMatch = matchScore;
    }

    /// <summary>
    /// Assign a stable name (only after stabilization criteria met).
    /// </summary>
    public void AssignStableName(string name)
    {
        if (IsStable)
        {
            _stableName = name;
        }
    }

    /// <summary>
    /// Mark as tracked (persisted across frames).
    /// </summary>
    public void MarkTracked() => _isTracked = true;

    /// <summary>
    /// Check if object matches another based on spatial/motion/color similarity.
    /// Used for tracking across frames.
    /// </summary>
    public float MatchScore(GradientObject other)
    {
        // Position proximity (predicted)
        float predictedX = _centroidX + _velocityX;
        float predictedY = _centroidY + _velocityY;
        float positionDist = MathF.Sqrt(
            (predictedX - other._centroidX) * (predictedX - other._centroidX) +
            (predictedY - other._centroidY) * (predictedY - other._centroidY));
        float positionScore = 1f / (1f + positionDist);

        // Size similarity
        float sizeRatio = _area > 0 ? other._area / _area : 1f;
        float sizeScore = 1f - Math.Abs(1f - sizeRatio);

        // Color similarity
        float hueDiff = Math.Min(Math.Abs(_dominantHue - other._dominantHue),
                                1f - Math.Abs(_dominantHue - other._dominantHue));
        float colorScore = 1f - hueDiff - Math.Abs(_meanSaturation - other._meanSaturation) * 0.3f;

        // Motion similarity
        float velocityDiff = MathF.Sqrt(
            (_velocityX - other._velocityX) * (_velocityX - other._velocityX) +
            (_velocityY - other._velocityY) * (_velocityY - other._velocityY));
        float motionScore = 1f / (1f + velocityDiff);

        return positionScore * 0.4f + sizeScore * 0.2f + colorScore * 0.2f + motionScore * 0.2f;
    }

    public override string ToString()
    {
        string identity = _stableName ?? (_prototypeId >= 0 ? $"Proto#{_prototypeId}" : "Unknown");
        return $"GradObj[{_objectId}]: {identity} pos=({_centroidX:F1},{_centroidY:F1}) " +
               $"vel=({_velocityX:F2},{_velocityY:F2}) area={_area:F0} " +
               $"hue={_dominantHue:F2} stability={_stability:F2} conf={_confidence:F2}";
    }
}

/// <summary>
/// Fixed-length signature vector for gradient object.
/// This is the numeric fingerprint used for prototype matching.
/// </summary>
public readonly struct SignatureVector
{
    // Motion (4 floats)
    public float VelocityX { get; init; }
    public float VelocityY { get; init; }
    public float Speed { get; init; }
    public float Acceleration { get; init; }

    // Shape (4 floats)
    public float Area { get; init; }
    public float AspectRatio { get; init; }
    public float Compactness { get; init; }
    public float EdgeDensity { get; init; }

    // Color (4 floats)
    public float DominantHue { get; init; }
    public float HueVariance { get; init; }
    public float Saturation { get; init; }
    public float Value { get; init; }

    // Temporal (3 floats)
    public int AgeFrames { get; init; }
    public float Jitter { get; init; }
    public float Stability { get; init; }

    // Context (3 floats)
    public float NormalizedX { get; init; }
    public float NormalizedY { get; init; }
    public float Confidence { get; init; }

    /// <summary>
    /// Convert to flat array for distance calculations.
    /// </summary>
    public float[] ToArray()
    {
        return new float[]
        {
            VelocityX, VelocityY, Speed, Acceleration,
            Area / 100f, AspectRatio, Compactness, EdgeDensity,
            DominantHue, HueVariance, Saturation, Value,
            AgeFrames / 100f, Jitter, Stability,
            NormalizedX, NormalizedY, Confidence
        };
    }

    /// <summary>
    /// Euclidean distance to another signature (normalized).
    /// </summary>
    public float DistanceTo(SignatureVector other)
    {
        var a = ToArray();
        var b = other.ToArray();

        float sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            float diff = a[i] - b[i];
            sum += diff * diff;
        }
        return MathF.Sqrt(sum);
    }

    /// <summary>
    /// Similarity score (0-1, 1=identical).
    /// </summary>
    public float SimilarityTo(SignatureVector other)
    {
        float dist = DistanceTo(other);
        return 1f / (1f + dist);
    }
}
