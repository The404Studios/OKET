using System.Text.Json;
using OKET.Core.State;
using OKET.Core.Actions;

namespace OKET.Runner.Logging;

/// <summary>
/// Logs episode data for training and analysis.
/// Records state-action pairs, outcomes, and episode boundaries.
/// </summary>
public sealed class EpisodeLogger : IDisposable
{
    private readonly string _logDirectory;
    private StreamWriter? _currentWriter;
    private string? _currentEpisodePath;
    private int _episodeCount;
    private long _stepCount;
    private readonly object _lock = new();

    public bool IsRecording { get; private set; }
    public int CurrentEpisode => _episodeCount;
    public long TotalSteps => _stepCount;

    public EpisodeLogger(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    /// <summary>
    /// Start a new episode recording.
    /// </summary>
    public void StartEpisode()
    {
        lock (_lock)
        {
            EndEpisode();

            _episodeCount++;
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _currentEpisodePath = Path.Combine(_logDirectory, $"episode_{_episodeCount:D4}_{timestamp}.jsonl");
            _currentWriter = new StreamWriter(_currentEpisodePath, append: false);
            IsRecording = true;

            // Write episode header
            var header = new
            {
                type = "episode_start",
                episode = _episodeCount,
                timestamp = DateTime.UtcNow
            };
            _currentWriter.WriteLine(JsonSerializer.Serialize(header));
        }
    }

    /// <summary>
    /// End the current episode.
    /// </summary>
    public void EndEpisode()
    {
        lock (_lock)
        {
            if (_currentWriter == null) return;

            // Write episode footer
            var footer = new
            {
                type = "episode_end",
                episode = _episodeCount,
                steps = _stepCount,
                timestamp = DateTime.UtcNow
            };
            _currentWriter.WriteLine(JsonSerializer.Serialize(footer));

            _currentWriter.Dispose();
            _currentWriter = null;
            IsRecording = false;
        }
    }

    /// <summary>
    /// Log a single step (state, action, outcome).
    /// </summary>
    public void LogStep(GameState state, ActionPlan plan, StepOutcome outcome)
    {
        lock (_lock)
        {
            if (_currentWriter == null) return;

            _stepCount++;

            var step = new StepRecord
            {
                Type = "step",
                FrameId = state.FrameId,
                Timestamp = state.Timestamp,
                State = new StateRecord
                {
                    Health = state.Hud.Health,
                    Armor = state.Hud.Armor,
                    AmmoClip = state.Hud.AmmoClip,
                    AmmoReserve = state.Hud.AmmoReserve,
                    Wave = state.Hud.Wave,
                    ThreatsInFov = state.ThreatsInFov,
                    NearestThreatDistance = state.NearestThreatDistance,
                    DangerLevel = state.DangerLevel,
                    HasTarget = state.Aim.Target != null,
                    IsOnTarget = state.Aim.IsOnTarget,
                    TargetConfidence = state.Aim.Target?.Confidence ?? 0,
                    FeatureVector = state.ToFeatureVector()
                },
                Action = new ActionRecord
                {
                    Mode = plan.Mode.ToString(),
                    ActionTypes = plan.Actions.Select(a => a.Type.ToString()).ToList(),
                    Confidence = plan.Confidence,
                    Reason = plan.Reason
                },
                Outcome = outcome
            };

            _currentWriter.WriteLine(JsonSerializer.Serialize(step));
        }
    }

    /// <summary>
    /// Log a significant event (death, kill, etc.).
    /// </summary>
    public void LogEvent(string eventType, Dictionary<string, object>? metadata = null)
    {
        lock (_lock)
        {
            if (_currentWriter == null) return;

            var evt = new
            {
                type = "event",
                eventType,
                timestamp = DateTime.UtcNow,
                metadata
            };
            _currentWriter.WriteLine(JsonSerializer.Serialize(evt));
        }
    }

    /// <summary>
    /// Flush pending writes.
    /// </summary>
    public void Flush()
    {
        lock (_lock)
        {
            _currentWriter?.Flush();
        }
    }

    public void Dispose()
    {
        EndEpisode();
    }

    private record StepRecord
    {
        public string Type { get; init; } = "step";
        public long FrameId { get; init; }
        public DateTime Timestamp { get; init; }
        public StateRecord State { get; init; } = new();
        public ActionRecord Action { get; init; } = new();
        public StepOutcome Outcome { get; init; } = new();
    }

    private record StateRecord
    {
        public int Health { get; init; }
        public int Armor { get; init; }
        public int AmmoClip { get; init; }
        public int AmmoReserve { get; init; }
        public int Wave { get; init; }
        public int ThreatsInFov { get; init; }
        public float NearestThreatDistance { get; init; }
        public float DangerLevel { get; init; }
        public bool HasTarget { get; init; }
        public bool IsOnTarget { get; init; }
        public float TargetConfidence { get; init; }
        public float[] FeatureVector { get; init; } = [];
    }

    private record ActionRecord
    {
        public string Mode { get; init; } = "";
        public List<string> ActionTypes { get; init; } = new();
        public float Confidence { get; init; }
        public string Reason { get; init; } = "";
    }
}

/// <summary>
/// Outcome/reward signal for a step.
/// </summary>
public record StepOutcome
{
    public float Reward { get; init; }
    public bool GotHit { get; init; }
    public bool DealtDamage { get; init; }
    public bool Died { get; init; }
    public int HealthDelta { get; init; }
}
