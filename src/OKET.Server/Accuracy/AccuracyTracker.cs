using OKET.Core.Types;
using OKET.Core.Prediction;

namespace OKET.Server.Accuracy;

/// <summary>
/// Tracks and computes accuracy metrics for agent predictions and actions.
/// Provides feedback for learning and debugging.
/// </summary>
public sealed class AccuracyTracker
{
    private readonly int _windowSize;
    private readonly Queue<AccuracyRecord> _records = new();
    private readonly Dictionary<string, MetricAccumulator> _metrics = new();

    /// <summary>Overall accuracy score [0, 1].</summary>
    public float OverallAccuracy { get; private set; }

    /// <summary>Position prediction accuracy.</summary>
    public float PositionAccuracy { get; private set; }

    /// <summary>Action success rate.</summary>
    public float ActionSuccessRate { get; private set; }

    /// <summary>Aim accuracy (shots that hit).</summary>
    public float AimAccuracy { get; private set; }

    /// <summary>Total samples recorded.</summary>
    public int TotalSamples { get; private set; }

    public AccuracyTracker(int windowSize = 1000)
    {
        _windowSize = windowSize;

        // Initialize metric accumulators
        _metrics["position"] = new MetricAccumulator("Position Prediction");
        _metrics["action"] = new MetricAccumulator("Action Success");
        _metrics["aim"] = new MetricAccumulator("Aim Accuracy");
        _metrics["threat"] = new MetricAccumulator("Threat Assessment");
        _metrics["reward"] = new MetricAccumulator("Reward Prediction");
    }

    /// <summary>
    /// Record a position prediction result.
    /// </summary>
    public void RecordPositionPrediction(int trackId, Vector2 predicted, Vector2 actual)
    {
        float error = Vector2.Distance(predicted, actual);
        float accuracy = Math.Clamp(1f - (error / 200f), 0f, 1f); // 200px = 0 accuracy

        AddRecord(new AccuracyRecord
        {
            Type = AccuracyType.Position,
            Accuracy = accuracy,
            Error = error,
            Timestamp = DateTime.UtcNow,
            Details = $"Track {trackId}: error={error:F1}px"
        });

        _metrics["position"].Add(accuracy);
        UpdateOverallMetrics();
    }

    /// <summary>
    /// Record an action outcome.
    /// </summary>
    public void RecordActionOutcome(string actionType, bool success, float rewardDelta)
    {
        AddRecord(new AccuracyRecord
        {
            Type = AccuracyType.Action,
            Accuracy = success ? 1f : 0f,
            Error = success ? 0f : 1f,
            Timestamp = DateTime.UtcNow,
            Details = $"{actionType}: {(success ? "success" : "fail")}, reward={rewardDelta:+0.00;-0.00;0.00}"
        });

        _metrics["action"].Add(success ? 1f : 0f);
        UpdateOverallMetrics();
    }

    /// <summary>
    /// Record aim accuracy (shot hit or miss).
    /// </summary>
    public void RecordShot(bool hit, float distanceToTarget)
    {
        AddRecord(new AccuracyRecord
        {
            Type = AccuracyType.Aim,
            Accuracy = hit ? 1f : 0f,
            Error = distanceToTarget,
            Timestamp = DateTime.UtcNow,
            Details = $"Shot: {(hit ? "HIT" : "MISS")} @ {distanceToTarget:F1}px"
        });

        _metrics["aim"].Add(hit ? 1f : 0f);
        UpdateOverallMetrics();
    }

    /// <summary>
    /// Record threat assessment accuracy.
    /// </summary>
    public void RecordThreatAssessment(int predictedCount, int actualCount, float predictedDistance, float actualDistance)
    {
        float countError = Math.Abs(predictedCount - actualCount);
        float distError = Math.Abs(predictedDistance - actualDistance);

        float countAccuracy = Math.Clamp(1f - (countError / 5f), 0f, 1f);
        float distAccuracy = Math.Clamp(1f - (distError / 200f), 0f, 1f);
        float combined = (countAccuracy + distAccuracy) / 2f;

        AddRecord(new AccuracyRecord
        {
            Type = AccuracyType.Threat,
            Accuracy = combined,
            Error = countError + distError,
            Timestamp = DateTime.UtcNow,
            Details = $"Threats: pred={predictedCount}, actual={actualCount}"
        });

        _metrics["threat"].Add(combined);
        UpdateOverallMetrics();
    }

    /// <summary>
    /// Record reward prediction accuracy.
    /// </summary>
    public void RecordRewardPrediction(float predicted, float actual)
    {
        float error = Math.Abs(predicted - actual);
        float accuracy = Math.Clamp(1f - error, 0f, 1f);

        AddRecord(new AccuracyRecord
        {
            Type = AccuracyType.Reward,
            Accuracy = accuracy,
            Error = error,
            Timestamp = DateTime.UtcNow,
            Details = $"Reward: pred={predicted:F2}, actual={actual:F2}"
        });

        _metrics["reward"].Add(accuracy);
        UpdateOverallMetrics();
    }

    /// <summary>
    /// Evaluate prediction accuracy against actual state.
    /// </summary>
    public AccuracyReport EvaluatePrediction(FramePrediction prediction,
        Dictionary<int, Vector2> actualPositions, int actualThreatCount, float actualNearestDistance)
    {
        var report = new AccuracyReport
        {
            FrameId = prediction.FrameId,
            Timestamp = DateTime.UtcNow
        };

        // Position predictions
        foreach (var (trackId, pred) in prediction.PredictedPositions)
        {
            if (actualPositions.TryGetValue(trackId, out var actual))
            {
                var error = Vector2.Distance(pred.PredictedPosition, actual);
                report.PositionErrors[trackId] = error;
                RecordPositionPrediction(trackId, pred.PredictedPosition, actual);
            }
        }

        // Threat count
        RecordThreatAssessment(
            prediction.PredictedThreatCount,
            actualThreatCount,
            prediction.PredictedNearestThreatDistance,
            actualNearestDistance);

        report.AveragePositionError = report.PositionErrors.Count > 0
            ? report.PositionErrors.Values.Average()
            : 0f;

        report.ThreatCountError = Math.Abs(prediction.PredictedThreatCount - actualThreatCount);
        report.DistanceError = Math.Abs(prediction.PredictedNearestThreatDistance - actualNearestDistance);

        return report;
    }

    /// <summary>
    /// Get summary of all accuracy metrics.
    /// </summary>
    public AccuracySummary GetSummary()
    {
        return new AccuracySummary
        {
            OverallAccuracy = OverallAccuracy,
            PositionAccuracy = _metrics["position"].Average,
            ActionSuccessRate = _metrics["action"].Average,
            AimAccuracy = _metrics["aim"].Average,
            ThreatAccuracy = _metrics["threat"].Average,
            RewardAccuracy = _metrics["reward"].Average,
            TotalSamples = TotalSamples,
            WindowSize = _windowSize,
            Metrics = _metrics.ToDictionary(
                kvp => kvp.Key,
                kvp => new MetricSummary
                {
                    Name = kvp.Value.Name,
                    Average = kvp.Value.Average,
                    Min = kvp.Value.Min,
                    Max = kvp.Value.Max,
                    Count = kvp.Value.Count
                })
        };
    }

    /// <summary>
    /// Get recent accuracy trend (positive = improving).
    /// </summary>
    public float GetTrend()
    {
        if (_records.Count < 20) return 0f;

        var recent = _records.TakeLast(10).Average(r => r.Accuracy);
        var older = _records.Skip(Math.Max(0, _records.Count - 20))
            .Take(10).Average(r => r.Accuracy);

        return recent - older;
    }

    /// <summary>
    /// Reset all accuracy tracking.
    /// </summary>
    public void Reset()
    {
        _records.Clear();
        foreach (var metric in _metrics.Values)
        {
            metric.Reset();
        }
        TotalSamples = 0;
        OverallAccuracy = 0f;
        PositionAccuracy = 0f;
        ActionSuccessRate = 0f;
        AimAccuracy = 0f;
    }

    private void AddRecord(AccuracyRecord record)
    {
        _records.Enqueue(record);
        TotalSamples++;

        while (_records.Count > _windowSize)
        {
            _records.Dequeue();
        }
    }

    private void UpdateOverallMetrics()
    {
        if (_records.Count == 0) return;

        OverallAccuracy = _records.Average(r => r.Accuracy);
        PositionAccuracy = _metrics["position"].Average;
        ActionSuccessRate = _metrics["action"].Average;
        AimAccuracy = _metrics["aim"].Average;
    }
}

/// <summary>
/// Single accuracy measurement record.
/// </summary>
internal sealed class AccuracyRecord
{
    public AccuracyType Type { get; init; }
    public float Accuracy { get; init; }
    public float Error { get; init; }
    public DateTime Timestamp { get; init; }
    public string Details { get; init; } = "";
}

/// <summary>
/// Types of accuracy being tracked.
/// </summary>
public enum AccuracyType
{
    Position,
    Action,
    Aim,
    Threat,
    Reward
}

/// <summary>
/// Accumulates values for a single metric.
/// </summary>
internal sealed class MetricAccumulator
{
    public string Name { get; }
    public float Average { get; private set; }
    public float Min { get; private set; } = float.MaxValue;
    public float Max { get; private set; } = float.MinValue;
    public int Count { get; private set; }

    private float _sum;

    public MetricAccumulator(string name)
    {
        Name = name;
    }

    public void Add(float value)
    {
        _sum += value;
        Count++;
        Average = _sum / Count;
        Min = Math.Min(Min, value);
        Max = Math.Max(Max, value);
    }

    public void Reset()
    {
        _sum = 0;
        Count = 0;
        Average = 0;
        Min = float.MaxValue;
        Max = float.MinValue;
    }
}

/// <summary>
/// Summary of accuracy metrics.
/// </summary>
public sealed class AccuracySummary
{
    public float OverallAccuracy { get; init; }
    public float PositionAccuracy { get; init; }
    public float ActionSuccessRate { get; init; }
    public float AimAccuracy { get; init; }
    public float ThreatAccuracy { get; init; }
    public float RewardAccuracy { get; init; }
    public int TotalSamples { get; init; }
    public int WindowSize { get; init; }
    public Dictionary<string, MetricSummary> Metrics { get; init; } = new();
}

/// <summary>
/// Summary for a single metric.
/// </summary>
public sealed class MetricSummary
{
    public string Name { get; init; } = "";
    public float Average { get; init; }
    public float Min { get; init; }
    public float Max { get; init; }
    public int Count { get; init; }
}

/// <summary>
/// Report from evaluating a prediction.
/// </summary>
public sealed class AccuracyReport
{
    public long FrameId { get; init; }
    public DateTime Timestamp { get; init; }
    public Dictionary<int, float> PositionErrors { get; } = new();
    public float AveragePositionError { get; set; }
    public int ThreatCountError { get; set; }
    public float DistanceError { get; set; }
}
