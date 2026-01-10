namespace OKET.Core.Cognition;

/// <summary>
/// Maintains running mean and standard deviation using Welford's algorithm.
/// Used for computing Z-scores incrementally without storing all data.
/// </summary>
public sealed class RunningStatistics
{
    private double _count;
    private double _mean;
    private double _m2; // Sum of squares of differences from mean

    public double Count => _count;
    public double Mean => _mean;
    public double Variance => _count > 1 ? _m2 / (_count - 1) : 0;
    public double StdDev => Math.Sqrt(Variance);

    /// <summary>
    /// Add a new sample.
    /// </summary>
    public void Add(double value)
    {
        _count++;
        double delta = value - _mean;
        _mean += delta / _count;
        double delta2 = value - _mean;
        _m2 += delta * delta2;
    }

    /// <summary>
    /// Compute Z-score for a value.
    /// </summary>
    public double ZScore(double value)
    {
        if (_count < 2 || StdDev < 1e-10)
            return 0;

        return (value - _mean) / StdDev;
    }

    /// <summary>
    /// Reset statistics.
    /// </summary>
    public void Reset()
    {
        _count = 0;
        _mean = 0;
        _m2 = 0;
    }
}

/// <summary>
/// Exponentially weighted running statistics.
/// More recent values have higher weight.
/// </summary>
public sealed class ExponentialStatistics
{
    private readonly double _alpha;
    private double _mean;
    private double _variance;
    private bool _initialized;

    /// <summary>
    /// Create with specified smoothing factor.
    /// </summary>
    /// <param name="alpha">Smoothing factor (0-1). Higher = more weight on recent.</param>
    public ExponentialStatistics(double alpha = 0.1)
    {
        _alpha = Math.Clamp(alpha, 0.001, 0.999);
    }

    public double Mean => _mean;
    public double Variance => _variance;
    public double StdDev => Math.Sqrt(Math.Max(_variance, 0));

    /// <summary>
    /// Add a new sample.
    /// </summary>
    public void Add(double value)
    {
        if (!_initialized)
        {
            _mean = value;
            _variance = 0;
            _initialized = true;
            return;
        }

        double diff = value - _mean;
        double increment = _alpha * diff;
        _mean += increment;
        _variance = (1 - _alpha) * (_variance + diff * increment);
    }

    /// <summary>
    /// Compute Z-score for a value.
    /// </summary>
    public double ZScore(double value)
    {
        if (!_initialized || StdDev < 1e-10)
            return 0;

        return (value - _mean) / StdDev;
    }

    /// <summary>
    /// Reset statistics.
    /// </summary>
    public void Reset()
    {
        _mean = 0;
        _variance = 0;
        _initialized = false;
    }
}

/// <summary>
/// Windowed statistics that only considers recent samples.
/// </summary>
public sealed class WindowedStatistics
{
    private readonly int _windowSize;
    private readonly Queue<double> _samples;

    public WindowedStatistics(int windowSize = 100)
    {
        _windowSize = windowSize;
        _samples = new Queue<double>(windowSize);
    }

    public int Count => _samples.Count;

    public double Mean => _samples.Count > 0 ? _samples.Average() : 0;

    public double Variance
    {
        get
        {
            if (_samples.Count < 2) return 0;
            double mean = Mean;
            return _samples.Average(x => (x - mean) * (x - mean));
        }
    }

    public double StdDev => Math.Sqrt(Variance);

    /// <summary>
    /// Add a new sample.
    /// </summary>
    public void Add(double value)
    {
        _samples.Enqueue(value);
        while (_samples.Count > _windowSize)
            _samples.Dequeue();
    }

    /// <summary>
    /// Compute Z-score for a value.
    /// </summary>
    public double ZScore(double value)
    {
        if (_samples.Count < 2 || StdDev < 1e-10)
            return 0;

        return (value - Mean) / StdDev;
    }

    /// <summary>
    /// Reset statistics.
    /// </summary>
    public void Reset()
    {
        _samples.Clear();
    }
}
