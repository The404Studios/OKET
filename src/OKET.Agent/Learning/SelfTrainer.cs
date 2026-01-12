using System.Text.Json;
using Microsoft.Extensions.Logging;
using OKET.Core.State;
using OKET.Core.Actions;

namespace OKET.Agent.Learning;

/// <summary>
/// Configuration for self-training.
/// </summary>
public sealed class TrainingConfig
{
    public string ModelDirectory { get; init; } = "models";
    public string CheckpointPrefix { get; init; } = "policy";
    public int SaveIntervalUpdates { get; init; } = 100;
    public int LogIntervalUpdates { get; init; } = 10;
    public int RolloutLength { get; init; } = 2048;
    public int TotalTimesteps { get; init; } = 1_000_000;
    public bool AutoSaveOnImprovement { get; init; } = true;
    public float InitialExplorationBonus { get; init; } = 0.1f;
    public int ExplorationDecaySteps { get; init; } = 100_000;
}

/// <summary>
/// Training session summary.
/// </summary>
public sealed class TrainingSession
{
    public DateTime StartTime { get; init; }
    public DateTime? EndTime { get; set; }
    public int TotalTimesteps { get; set; }
    public int TotalEpisodes { get; set; }
    public int TotalUpdates { get; set; }
    public float BestMeanReward { get; set; } = float.MinValue;
    public float LatestMeanReward { get; set; }
    public List<float> RewardHistory { get; } = new();
    public List<float> LossHistory { get; } = new();
}

/// <summary>
/// Self-trainer orchestrator that manages the entire training lifecycle.
/// Collects experiences during gameplay and periodically trains the policy.
/// </summary>
public sealed class SelfTrainer : IDisposable
{
    private readonly ILogger<SelfTrainer>? _logger;
    private readonly NeuralPolicy _policy;
    private readonly PPOTrainer _trainer;
    private readonly ExperienceBuffer _buffer;
    private readonly TrainingConfig _config;
    private readonly TrainingSession _session;

    // Current trajectory being collected
    private readonly Trajectory _currentTrajectory = new();
    private float[]? _lastState;
    private int _lastAction;
    private float _lastLogProb;
    private float _lastValue;

    // Statistics
    private float _episodeReward;
    private int _episodeLength;
    private readonly Queue<float> _recentRewards = new();
    private readonly Queue<int> _recentLengths = new();
    private const int RecentWindowSize = 100;

    // State
    private bool _isCollecting;
    private int _timestepsSinceUpdate;

    public NeuralPolicy Policy => _policy;
    public TrainingSession Session => _session;
    public bool IsCollecting => _isCollecting;
    public int TimestepsSinceUpdate => _timestepsSinceUpdate;
    public float MeanRecentReward => _recentRewards.Count > 0 ? _recentRewards.Average() : 0;
    public float MeanRecentLength => _recentLengths.Count > 0 ? (float)_recentLengths.Average() : 0;

    public SelfTrainer(
        NeuralPolicy? policy = null,
        PPOConfig? ppoConfig = null,
        TrainingConfig? trainingConfig = null,
        ILogger<SelfTrainer>? logger = null)
    {
        _logger = logger;
        _config = trainingConfig ?? new TrainingConfig();
        _policy = policy ?? new NeuralPolicy();
        _trainer = new PPOTrainer(_policy, ppoConfig);
        _buffer = new ExperienceBuffer(_config.RolloutLength * 2);
        _session = new TrainingSession { StartTime = DateTime.UtcNow };

        Directory.CreateDirectory(_config.ModelDirectory);
    }

    /// <summary>
    /// Start collecting experiences for training.
    /// </summary>
    public void StartCollecting()
    {
        _isCollecting = true;
        _policy.SetTrainingMode(true);
        _logger?.LogInformation("Started collecting experiences for training");
    }

    /// <summary>
    /// Stop collecting experiences.
    /// </summary>
    public void StopCollecting()
    {
        _isCollecting = false;
        _logger?.LogInformation("Stopped collecting experiences");
    }

    /// <summary>
    /// Called at the start of each step to get the action.
    /// Returns the action to take and stores state for later experience creation.
    /// </summary>
    public (StrategicMode mode, float confidence) BeginStep(GameState state)
    {
        if (!_isCollecting)
        {
            return _policy.Decide(state);
        }

        var features = state.ToFeatureVector();
        var (action, logProb, value) = _policy.SampleAction(features);

        // Store for experience creation
        _lastState = features;
        _lastAction = action;
        _lastLogProb = logProb;
        _lastValue = value;

        // Add exploration bonus early in training (encourages trying different strategies)
        float explorationBonus = GetExplorationBonus();
        if (Random.Shared.NextDouble() < explorationBonus)
        {
            // Random exploration
            action = Random.Shared.Next(NeuralPolicy.NumActions);
        }

        var mode = NeuralPolicy.ActionToMode(action);
        var confidence = _policy.GetActionProbabilities(features)[action];

        return (mode, confidence);
    }

    /// <summary>
    /// Called at the end of each step to record the outcome.
    /// </summary>
    public void EndStep(GameState nextState, float reward, bool done)
    {
        if (!_isCollecting || _lastState == null)
            return;

        var nextFeatures = nextState.ToFeatureVector();

        // Create experience
        var experience = new Experience
        {
            State = _lastState,
            Action = _lastAction,
            Reward = reward,
            NextState = nextFeatures,
            Done = done,
            LogProbability = _lastLogProb,
            Value = _lastValue
        };

        _currentTrajectory.Add(experience);
        _buffer.Add(experience);

        _episodeReward += reward;
        _episodeLength++;
        _timestepsSinceUpdate++;
        _session.TotalTimesteps++;

        // Check if episode ended
        if (done)
        {
            EndEpisode();
        }

        // Check if it's time to update
        if (_timestepsSinceUpdate >= _config.RolloutLength)
        {
            PerformUpdate();
        }

        _lastState = null;
    }

    /// <summary>
    /// Called when an episode ends.
    /// </summary>
    private void EndEpisode()
    {
        _session.TotalEpisodes++;

        // Track recent rewards
        _recentRewards.Enqueue(_episodeReward);
        _recentLengths.Enqueue(_episodeLength);
        while (_recentRewards.Count > RecentWindowSize) _recentRewards.Dequeue();
        while (_recentLengths.Count > RecentWindowSize) _recentLengths.Dequeue();

        _logger?.LogDebug(
            "Episode {Episode}: Reward={Reward:F2}, Length={Length}, MeanReward={Mean:F2}",
            _session.TotalEpisodes, _episodeReward, _episodeLength, MeanRecentReward);

        // Reset episode tracking
        _episodeReward = 0;
        _episodeLength = 0;
        _currentTrajectory.Clear();
    }

    /// <summary>
    /// Perform a PPO update using collected experiences.
    /// </summary>
    public TrainingStats PerformUpdate()
    {
        var experiences = _buffer.DrainAll();
        if (experiences.Length == 0)
            return default;

        _logger?.LogInformation("Performing PPO update with {Count} experiences", experiences.Length);

        var stats = _trainer.Update(experiences);
        _session.TotalUpdates++;
        _timestepsSinceUpdate = 0;

        // Track statistics
        _session.LatestMeanReward = MeanRecentReward;
        _session.RewardHistory.Add(MeanRecentReward);
        _session.LossHistory.Add(stats.TotalLoss);

        // Log progress
        if (_session.TotalUpdates % _config.LogIntervalUpdates == 0)
        {
            _logger?.LogInformation(
                "Update {Update}: PolicyLoss={PLoss:F4}, ValueLoss={VLoss:F4}, Entropy={Ent:F4}, " +
                "MeanReward={Reward:F2}, ClipFrac={Clip:F3}",
                _session.TotalUpdates, stats.PolicyLoss, stats.ValueLoss, stats.EntropyLoss,
                MeanRecentReward, stats.ClipFraction);
        }

        // Save checkpoint
        if (_session.TotalUpdates % _config.SaveIntervalUpdates == 0)
        {
            SaveCheckpoint();
        }

        // Auto-save on improvement
        if (_config.AutoSaveOnImprovement && MeanRecentReward > _session.BestMeanReward)
        {
            _session.BestMeanReward = MeanRecentReward;
            SaveCheckpoint("best");
            _logger?.LogInformation("New best model saved with reward {Reward:F2}", MeanRecentReward);
        }

        return stats;
    }

    /// <summary>
    /// Get exploration bonus based on training progress.
    /// </summary>
    private float GetExplorationBonus()
    {
        float progress = Math.Min(1f, (float)_session.TotalTimesteps / _config.ExplorationDecaySteps);
        return _config.InitialExplorationBonus * (1f - progress);
    }

    /// <summary>
    /// Save a checkpoint of the current model.
    /// </summary>
    public void SaveCheckpoint(string? suffix = null)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var filename = suffix != null
            ? $"{_config.CheckpointPrefix}_{suffix}.pt"
            : $"{_config.CheckpointPrefix}_{timestamp}_update{_session.TotalUpdates}.pt";

        var path = Path.Combine(_config.ModelDirectory, filename);
        _policy.SaveModel(path);

        // Save training session metadata
        var metaPath = Path.ChangeExtension(path, ".json");
        var metadata = new
        {
            _session.TotalTimesteps,
            _session.TotalEpisodes,
            _session.TotalUpdates,
            _session.BestMeanReward,
            _session.LatestMeanReward,
            Timestamp = DateTime.UtcNow
        };
        File.WriteAllText(metaPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

        _logger?.LogInformation("Saved checkpoint to {Path}", path);
    }

    /// <summary>
    /// Load a checkpoint.
    /// </summary>
    public void LoadCheckpoint(string path)
    {
        _policy.LoadModel(path);
        _logger?.LogInformation("Loaded checkpoint from {Path}", path);

        // Try to load metadata
        var metaPath = Path.ChangeExtension(path, ".json");
        if (File.Exists(metaPath))
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(metaPath));
                _logger?.LogInformation("Loaded metadata: {Metadata}", metadata);
            }
            catch { }
        }
    }

    /// <summary>
    /// Load the best saved model if it exists.
    /// </summary>
    public bool LoadBestModel()
    {
        var bestPath = Path.Combine(_config.ModelDirectory, $"{_config.CheckpointPrefix}_best.pt");
        if (File.Exists(bestPath))
        {
            LoadCheckpoint(bestPath);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Train from logged episode data (offline training).
    /// </summary>
    public void TrainFromLogs(string logDirectory, int numEpochs = 10)
    {
        _logger?.LogInformation("Starting offline training from logs in {Dir}", logDirectory);

        var logFiles = Directory.GetFiles(logDirectory, "episode_*.jsonl");
        _logger?.LogInformation("Found {Count} episode files", logFiles.Length);

        foreach (var logFile in logFiles)
        {
            try
            {
                LoadEpisodeFromLog(logFile);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load {File}", logFile);
            }
        }

        // Perform multiple training epochs on loaded data
        for (int epoch = 0; epoch < numEpochs; epoch++)
        {
            var stats = PerformUpdate();
            _logger?.LogInformation(
                "Offline epoch {Epoch}: Loss={Loss:F4}, Reward={Reward:F2}",
                epoch + 1, stats.TotalLoss, stats.MeanReturn);
        }
    }

    /// <summary>
    /// Load experiences from a logged episode file.
    /// </summary>
    private void LoadEpisodeFromLog(string logFile)
    {
        foreach (var line in File.ReadLines(logFile))
        {
            try
            {
                var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var type) && type.GetString() == "step")
                {
                    // Extract state features
                    var stateElement = root.GetProperty("State");
                    var featureVector = stateElement.GetProperty("FeatureVector");
                    var state = featureVector.EnumerateArray().Select(e => e.GetSingle()).ToArray();

                    // Extract action
                    var actionElement = root.GetProperty("Action");
                    var modeStr = actionElement.GetProperty("Mode").GetString() ?? "Idle";
                    var mode = Enum.Parse<StrategicMode>(modeStr);
                    var action = NeuralPolicy.ModeToAction(mode);

                    // Extract reward
                    var outcome = root.GetProperty("Outcome");
                    var reward = outcome.GetProperty("Reward").GetSingle();
                    var done = outcome.GetProperty("Died").GetBoolean();

                    // Create experience (with placeholder values for log prob and value)
                    var experience = new Experience
                    {
                        State = state,
                        Action = action,
                        Reward = reward,
                        NextState = state, // Approximate - use same state
                        Done = done,
                        LogProbability = 0, // Will be recomputed
                        Value = 0 // Will be recomputed
                    };

                    _buffer.Add(experience);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Set the policy to evaluation mode (no exploration).
    /// </summary>
    public void SetEvaluationMode(bool eval = true)
    {
        _policy.SetTrainingMode(!eval);
        if (eval)
        {
            _isCollecting = false;
        }
    }

    public string GetDiagnostics() => $"""
        === SELF-TRAINER ===
        Timesteps: {_session.TotalTimesteps:N0}
        Episodes: {_session.TotalEpisodes:N0}
        Updates: {_session.TotalUpdates:N0}
        Mean Reward (100 ep): {MeanRecentReward:F2}
        Best Reward: {_session.BestMeanReward:F2}
        Mean Length (100 ep): {MeanRecentLength:F1}
        Exploration Bonus: {GetExplorationBonus():F3}
        Collecting: {_isCollecting}
        {_buffer.GetDiagnostics()}
        {_trainer.GetDiagnostics()}
        {_policy.GetDiagnostics()}
        ====================
        """;

    public void Dispose()
    {
        _session.EndTime = DateTime.UtcNow;

        // Save final checkpoint
        if (_session.TotalUpdates > 0)
        {
            SaveCheckpoint("final");
        }

        _trainer.Dispose();
        _policy.Dispose();
    }
}
