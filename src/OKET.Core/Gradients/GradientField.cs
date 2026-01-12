namespace OKET.Core.Gradients;

/// <summary>
/// Local Layer: Raw Perception Fields.
///
/// PRINCIPLE: No boxes, no labels. Just measurable gradients.
///
/// From each frame (or ROI), compute feature fields (2D grids):
/// - I(x,y) intensity (grayscale)
/// - E(x,y) edge magnitude
/// - F(x,y) flow vector (optical flow)
/// - H(x,y), S(x,y), V(x,y) color channels
/// - T(x,y) texture (variance)
///
/// These are the "local nodes" - the raw data before any interpretation.
/// </summary>
public sealed class GradientField
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _cellSize;
    private readonly int _gridWidth;
    private readonly int _gridHeight;

    // Raw field data (downsampled to grid)
    private readonly float[] _intensity;      // I(x,y) - grayscale
    private readonly float[] _edgeMagnitude;  // E(x,y) - edge strength
    private readonly float[] _flowX;          // Fx(x,y) - horizontal flow
    private readonly float[] _flowY;          // Fy(x,y) - vertical flow
    private readonly float[] _hue;            // H(x,y) - color hue
    private readonly float[] _saturation;     // S(x,y) - color saturation
    private readonly float[] _value;          // V(x,y) - color value/brightness
    private readonly float[] _texture;        // T(x,y) - local variance

    // Temporal tracking
    private readonly float[] _prevIntensity;
    private readonly float[] _temporalChange;
    private long _frameId;

    public int Width => _width;
    public int Height => _height;
    public int GridWidth => _gridWidth;
    public int GridHeight => _gridHeight;
    public int CellSize => _cellSize;
    public long FrameId => _frameId;

    public GradientField(int width, int height, int cellSize = 16)
    {
        _width = width;
        _height = height;
        _cellSize = cellSize;
        _gridWidth = (width + cellSize - 1) / cellSize;
        _gridHeight = (height + cellSize - 1) / cellSize;

        int gridSize = _gridWidth * _gridHeight;

        _intensity = new float[gridSize];
        _edgeMagnitude = new float[gridSize];
        _flowX = new float[gridSize];
        _flowY = new float[gridSize];
        _hue = new float[gridSize];
        _saturation = new float[gridSize];
        _value = new float[gridSize];
        _texture = new float[gridSize];
        _prevIntensity = new float[gridSize];
        _temporalChange = new float[gridSize];
    }

    /// <summary>
    /// Update field from raw frame data.
    /// </summary>
    public void Update(FrameData frame, long frameId)
    {
        _frameId = frameId;

        // Store previous for temporal diff
        Array.Copy(_intensity, _prevIntensity, _intensity.Length);

        // Process each grid cell
        for (int gy = 0; gy < _gridHeight; gy++)
        {
            for (int gx = 0; gx < _gridWidth; gx++)
            {
                int idx = gy * _gridWidth + gx;
                ProcessCell(frame, gx, gy, idx);
            }
        }

        // Compute temporal change
        for (int i = 0; i < _intensity.Length; i++)
        {
            _temporalChange[i] = Math.Abs(_intensity[i] - _prevIntensity[i]);
        }
    }

    private void ProcessCell(FrameData frame, int gx, int gy, int idx)
    {
        int startX = gx * _cellSize;
        int startY = gy * _cellSize;
        int endX = Math.Min(startX + _cellSize, _width);
        int endY = Math.Min(startY + _cellSize, _height);

        float sumI = 0, sumE = 0, sumH = 0, sumS = 0, sumV = 0;
        float sumFx = 0, sumFy = 0;
        float sumSqI = 0;
        int count = 0;

        for (int y = startY; y < endY; y++)
        {
            for (int x = startX; x < endX; x++)
            {
                int pixIdx = y * _width + x;

                // Get pixel values from frame
                float i = frame.GetIntensity(x, y);
                float e = frame.GetEdge(x, y);
                var (h, s, v) = frame.GetHSV(x, y);
                var (fx, fy) = frame.GetFlow(x, y);

                sumI += i;
                sumE += e;
                sumH += h;
                sumS += s;
                sumV += v;
                sumFx += fx;
                sumFy += fy;
                sumSqI += i * i;
                count++;
            }
        }

        if (count > 0)
        {
            float meanI = sumI / count;
            _intensity[idx] = meanI;
            _edgeMagnitude[idx] = sumE / count;
            _hue[idx] = sumH / count;
            _saturation[idx] = sumS / count;
            _value[idx] = sumV / count;
            _flowX[idx] = sumFx / count;
            _flowY[idx] = sumFy / count;

            // Texture = local variance
            float variance = (sumSqI / count) - (meanI * meanI);
            _texture[idx] = MathF.Sqrt(Math.Max(0, variance));
        }
    }

    /// <summary>
    /// Get all field values at a grid position.
    /// </summary>
    public FieldSample GetSample(int gx, int gy)
    {
        if (gx < 0 || gx >= _gridWidth || gy < 0 || gy >= _gridHeight)
            return default;

        int idx = gy * _gridWidth + gx;
        return new FieldSample
        {
            Intensity = _intensity[idx],
            EdgeMagnitude = _edgeMagnitude[idx],
            FlowX = _flowX[idx],
            FlowY = _flowY[idx],
            Hue = _hue[idx],
            Saturation = _saturation[idx],
            Value = _value[idx],
            Texture = _texture[idx],
            TemporalChange = _temporalChange[idx],
            GridX = gx,
            GridY = gy
        };
    }

    /// <summary>
    /// Get samples in a region.
    /// </summary>
    public IEnumerable<FieldSample> GetRegion(int gx, int gy, int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int nx = gx + dx;
                int ny = gy + dy;
                if (nx >= 0 && nx < _gridWidth && ny >= 0 && ny < _gridHeight)
                {
                    yield return GetSample(nx, ny);
                }
            }
        }
    }

    /// <summary>
    /// Find cells with high activity (motion, edges, or temporal change).
    /// </summary>
    public IEnumerable<(int gx, int gy, float activity)> FindActiveCells(float threshold = 0.1f)
    {
        for (int gy = 0; gy < _gridHeight; gy++)
        {
            for (int gx = 0; gx < _gridWidth; gx++)
            {
                int idx = gy * _gridWidth + gx;

                float flowMag = MathF.Sqrt(_flowX[idx] * _flowX[idx] + _flowY[idx] * _flowY[idx]);
                float activity = Math.Max(Math.Max(_edgeMagnitude[idx], flowMag), _temporalChange[idx]);

                if (activity > threshold)
                {
                    yield return (gx, gy, activity);
                }
            }
        }
    }

    /// <summary>
    /// Compute gradient direction at a cell.
    /// </summary>
    public (float dx, float dy) GetGradientDirection(int gx, int gy)
    {
        // Sobel-like gradient from intensity field
        float left = gx > 0 ? _intensity[gy * _gridWidth + gx - 1] : _intensity[gy * _gridWidth + gx];
        float right = gx < _gridWidth - 1 ? _intensity[gy * _gridWidth + gx + 1] : _intensity[gy * _gridWidth + gx];
        float up = gy > 0 ? _intensity[(gy - 1) * _gridWidth + gx] : _intensity[gy * _gridWidth + gx];
        float down = gy < _gridHeight - 1 ? _intensity[(gy + 1) * _gridWidth + gx] : _intensity[gy * _gridWidth + gx];

        return (right - left, down - up);
    }

    /// <summary>
    /// Get raw field arrays for bulk processing.
    /// </summary>
    public ReadOnlySpan<float> GetIntensityField() => _intensity;
    public ReadOnlySpan<float> GetEdgeField() => _edgeMagnitude;
    public ReadOnlySpan<float> GetFlowXField() => _flowX;
    public ReadOnlySpan<float> GetFlowYField() => _flowY;
    public ReadOnlySpan<float> GetHueField() => _hue;
    public ReadOnlySpan<float> GetSaturationField() => _saturation;
    public ReadOnlySpan<float> GetValueField() => _value;
    public ReadOnlySpan<float> GetTextureField() => _texture;
    public ReadOnlySpan<float> GetTemporalChangeField() => _temporalChange;
}

/// <summary>
/// Sample of all field values at a single grid position.
/// </summary>
public readonly struct FieldSample
{
    public float Intensity { get; init; }
    public float EdgeMagnitude { get; init; }
    public float FlowX { get; init; }
    public float FlowY { get; init; }
    public float Hue { get; init; }
    public float Saturation { get; init; }
    public float Value { get; init; }
    public float Texture { get; init; }
    public float TemporalChange { get; init; }
    public int GridX { get; init; }
    public int GridY { get; init; }

    /// <summary>Flow magnitude.</summary>
    public float FlowMagnitude => MathF.Sqrt(FlowX * FlowX + FlowY * FlowY);

    /// <summary>Flow direction in radians.</summary>
    public float FlowDirection => MathF.Atan2(FlowY, FlowX);

    /// <summary>Overall activity level.</summary>
    public float Activity => Math.Max(Math.Max(EdgeMagnitude, FlowMagnitude), TemporalChange);

    /// <summary>Is this cell "interesting" (has signal)?</summary>
    public bool HasSignal => Activity > 0.05f || Saturation > 0.2f;

    /// <summary>Similarity to another sample (0-1, 1=identical).</summary>
    public float SimilarityTo(FieldSample other)
    {
        float dI = Math.Abs(Intensity - other.Intensity);
        float dE = Math.Abs(EdgeMagnitude - other.EdgeMagnitude);
        float dH = Math.Min(Math.Abs(Hue - other.Hue), 1f - Math.Abs(Hue - other.Hue)); // Circular
        float dS = Math.Abs(Saturation - other.Saturation);
        float dV = Math.Abs(Value - other.Value);
        float dFlow = MathF.Sqrt((FlowX - other.FlowX) * (FlowX - other.FlowX) +
                                 (FlowY - other.FlowY) * (FlowY - other.FlowY));

        float totalDiff = (dI + dE + dH + dS + dV + dFlow * 0.5f) / 5.5f;
        return Math.Max(0, 1f - totalDiff);
    }
}

/// <summary>
/// Raw frame data interface for field computation.
/// </summary>
public interface FrameData
{
    float GetIntensity(int x, int y);
    float GetEdge(int x, int y);
    (float h, float s, float v) GetHSV(int x, int y);
    (float fx, float fy) GetFlow(int x, int y);
}

/// <summary>
/// Simple frame data implementation for testing/simulation.
/// </summary>
public sealed class SimpleFrameData : FrameData
{
    private readonly float[] _intensity;
    private readonly float[] _edges;
    private readonly float[] _hue;
    private readonly float[] _saturation;
    private readonly float[] _value;
    private readonly float[] _flowX;
    private readonly float[] _flowY;
    private readonly int _width;

    public SimpleFrameData(int width, int height)
    {
        _width = width;
        int size = width * height;
        _intensity = new float[size];
        _edges = new float[size];
        _hue = new float[size];
        _saturation = new float[size];
        _value = new float[size];
        _flowX = new float[size];
        _flowY = new float[size];
    }

    public void SetPixel(int x, int y, float intensity, float edge,
        float h, float s, float v, float fx = 0, float fy = 0)
    {
        int idx = y * _width + x;
        _intensity[idx] = intensity;
        _edges[idx] = edge;
        _hue[idx] = h;
        _saturation[idx] = s;
        _value[idx] = v;
        _flowX[idx] = fx;
        _flowY[idx] = fy;
    }

    public float GetIntensity(int x, int y) => _intensity[y * _width + x];
    public float GetEdge(int x, int y) => _edges[y * _width + x];
    public (float h, float s, float v) GetHSV(int x, int y)
    {
        int idx = y * _width + x;
        return (_hue[idx], _saturation[idx], _value[idx]);
    }
    public (float fx, float fy) GetFlow(int x, int y)
    {
        int idx = y * _width + x;
        return (_flowX[idx], _flowY[idx]);
    }
}
