using System.Diagnostics;

namespace OKET.Runner.Logging;

/// <summary>
/// Monitors and reports agent performance metrics.
/// </summary>
public sealed class PerformanceMonitor
{
    private readonly Stopwatch _frameTimer = new();
    private readonly Queue<double> _frameTimes = new();
    private readonly Queue<double> _perceptionTimes = new();
    private readonly Queue<double> _decisionTimes = new();
    private readonly Queue<double> _actuationTimes = new();

    private const int SampleCount = 100;

    public double AverageFrameTimeMs => _frameTimes.Count > 0 ? _frameTimes.Average() : 0;
    public double AveragePerceptionMs => _perceptionTimes.Count > 0 ? _perceptionTimes.Average() : 0;
    public double AverageDecisionMs => _decisionTimes.Count > 0 ? _decisionTimes.Average() : 0;
    public double AverageActuationMs => _actuationTimes.Count > 0 ? _actuationTimes.Average() : 0;
    public double CurrentFps => AverageFrameTimeMs > 0 ? 1000.0 / AverageFrameTimeMs : 0;

    public long TotalFrames { get; private set; }
    public TimeSpan TotalRuntime { get; private set; }
    public DateTime StartTime { get; private set; }

    private Stopwatch _runtimeStopwatch = new();

    public void Start()
    {
        StartTime = DateTime.UtcNow;
        _runtimeStopwatch.Start();
    }

    public void Stop()
    {
        _runtimeStopwatch.Stop();
        TotalRuntime = _runtimeStopwatch.Elapsed;
    }

    public void BeginFrame()
    {
        _frameTimer.Restart();
    }

    public void EndFrame()
    {
        _frameTimer.Stop();
        AddSample(_frameTimes, _frameTimer.Elapsed.TotalMilliseconds);
        TotalFrames++;
    }

    public void RecordPerceptionTime(double ms) => AddSample(_perceptionTimes, ms);
    public void RecordDecisionTime(double ms) => AddSample(_decisionTimes, ms);
    public void RecordActuationTime(double ms) => AddSample(_actuationTimes, ms);

    private void AddSample(Queue<double> queue, double value)
    {
        queue.Enqueue(value);
        while (queue.Count > SampleCount)
            queue.Dequeue();
    }

    public string GetSummary()
    {
        return $"""
            Performance Summary:
              Total Frames: {TotalFrames:N0}
              Runtime: {TotalRuntime:hh\:mm\:ss}
              Average FPS: {CurrentFps:F1}
              Frame Time: {AverageFrameTimeMs:F2}ms
              Perception: {AveragePerceptionMs:F2}ms
              Decision: {AverageDecisionMs:F2}ms
              Actuation: {AverageActuationMs:F2}ms
            """;
    }
}
