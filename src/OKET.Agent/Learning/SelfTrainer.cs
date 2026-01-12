using System.Text.Json;
using Microsoft.Extensions.Logging;
using OKET.Core.State;
using OKET.Core.Actions;
using OKET.Agent.Learning.Knowledge;

namespace OKET.Agent.Learning;

/// <summary>
/// Configuration for self-training.
/// </summary>
public sealed class TrainingConfig
{
    public string ModelDirectory { get; init; } = "models";
    public string KnowledgeDirectory { get; init; } = "knowledge";
    public string CheckpointPrefix { get; init; } = "policy";
    public int SaveIntervalUpdates { get; init; } = 100;
    public int LogIntervalUpdates { get; init; } = 10;
    public int RolloutLength { get; init; } = 2048;
    public int TotalTimesteps { get; init; } = 1_000_000;
    public bool AutoSaveOnImprovement { get; init; } = true;
    public float InitialExplorationBonus { get; init; } = 0.1f;
    public int ExplorationDecaySteps { get; init; } = 100_000;
    public bool EnableKnowledgeOrganizer { get; init; } = true;
    public int KnowledgeDiscoveryInterval { get; init; } = 1000;
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
/// Implements the knowledge cycle: GAIN → HOLD → CARRY → MULTIPLY → EMBODY → LOOP
///
/// - GAIN: Acquire new knowledge through pattern detection
/// - HOLD: Persist knowledge in hierarchical organization (Laws→Rules→...→Traditions)
/// - CARRY: Transfer knowledge across contexts via knowledge queries
/// - MULTIPLY: Spread successful patterns through promotion
/// - EMBODY: Internalize knowledge into neural policy behavior
/// - LOOP: Continuous refinement through observation and reorganization
///
/// Valence System Integration:
/// - AUTHORIZE: Validate mode transitions through ValenceAuthorizer
/// - METABOLIZE: Process experiences into valence signals
/// - RECALIBRATE: Enter neutral mode when signals are uncertain
/// </summary>
public sealed class SelfTrainer : IDisposable
{
    private readonly ILogger<SelfTrainer>? _logger;
    private readonly NeuralPolicy _policy;
    private readonly PPOTrainer _trainer;
    private readonly ExperienceBuffer _buffer;
    private readonly TrainingConfig _config;
    private readonly TrainingSession _session;

    // Knowledge organization (Law of Potential)
    private readonly KnowledgeOrganizer? _knowledge;

    // Valence system for emotional/motivational state
    private readonly ValenceAuthorizer _valenceAuthorizer;
    private readonly ValenceMetabolizer _valenceMetabolizer;

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
    public KnowledgeOrganizer? Knowledge => _knowledge;
    public ValenceAuthorizer ValenceAuthorizer => _valenceAuthorizer;
    public ValenceMetabolizer ValenceMetabolizer => _valenceMetabolizer;
    public ValenceState CurrentValence => _valenceAuthorizer.CurrentState;
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

        // Initialize valence system for emotional/motivational learning
        _valenceAuthorizer = new ValenceAuthorizer(
            positiveThreshold: 0.4f,
            negativeThreshold: -0.4f,
            neutralThreshold: 0.15f,
            minDurationBeforeSwitch: 10);
        _valenceMetabolizer = new ValenceMetabolizer(
            rewardWeight: 0.4f,
            healthWeight: 0.3f,
            threatWeight: 0.2f,
            progressWeight: 0.1f);

        _logger?.LogInformation("Valence system initialized (Positive/Negative/Neutral modes)");

        // Initialize knowledge organizer if enabled
        if (_config.EnableKnowledgeOrganizer)
        {
            var knowledgePath = Path.Combine(_config.KnowledgeDirectory, "knowledge_base.json");
            _knowledge = new KnowledgeOrganizer(
                discoveryInterval: _config.KnowledgeDiscoveryInterval,
                persistencePath: knowledgePath);

            // Try to load existing knowledge
            if (_knowledge.Load())
            {
                _logger?.LogInformation("Loaded existing knowledge base with {Count} units", _knowledge.KnowledgeCount);
            }
            else
            {
                _logger?.LogInformation("Starting with fresh knowledge base");
            }
        }
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
    /// Integrates knowledge-based guidance with neural policy (CARRY + EMBODY).
    /// Now includes VALENCE AUTHORIZATION for mode transitions.
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

        // CARRY: Query knowledge for guidance
        if (_knowledge != null)
        {
            var suggestion = _knowledge.GetSuggestedAction(features);
            if (suggestion.HasValue && suggestion.Value.confidence > 0.8f)
            {
                // High-confidence knowledge overrides policy (EMBODY)
                action = suggestion.Value.action;
                _logger?.LogDebug("Knowledge override: {Reason}", suggestion.Value.reason);
            }
            else
            {
                // Get action modifiers from covenants and principles
                var modifiers = _knowledge.GetActionModifiers(features);
                if (modifiers.TryGetValue("risk_aversion", out var riskAversion) && riskAversion > 0.5f)
                {
                    // Knowledge says be cautious - bias toward defensive actions
                    if (action == 1) // Fight
                        action = Random.Shared.NextDouble() < riskAversion ? 2 : action; // Maybe Kite instead
                }
            }
        }

        // VALENCE AUTHORIZATION: Check if action aligns with current valence state
        var proposedValence = ActionValenceMapping.GetNaturalValence(action);
        var currentValenceState = _valenceAuthorizer.CurrentState;

        // Calculate urgency based on game state
        float health = features.Length > FeatureIndices.Health ? features[FeatureIndices.Health] : 1f;
        float dangerLevel = features.Length > FeatureIndices.DangerLevel ? features[FeatureIndices.DangerLevel] : 0f;
        float urgency = dangerLevel > 0.7f || health < 0.3f ? 0.9f : dangerLevel;

        // Get metabolizer's recommended valence based on accumulated experience
        var recommendedValence = _valenceMetabolizer.GetRecommendedValence(features);

        // Calculate signal strength based on state
        float valenceSignal = CalculateValenceSignal(features, action);

        // Request authorization for the proposed valence
        var authorization = _valenceAuthorizer.RequestTransition(proposedValence, valenceSignal, urgency);

        // If not authorized, consider alternative actions based on authorized valence
        if (!authorization.IsAuthorized && !authorization.RequiresRecalibration)
        {
            // Switch to an action that matches the authorized/current valence
            var authorizedActions = ActionValenceMapping.GetActionsForValence(authorization.AuthorizedValence);
            if (authorizedActions.Length > 0)
            {
                action = authorizedActions[Random.Shared.Next(authorizedActions.Length)];
                _logger?.LogDebug("Valence redirect: {Reason} -> action {Action}",
                    authorization.Reason, action);
            }
        }

        // If recalibration required, prefer neutral actions
        if (authorization.RequiresRecalibration)
        {
            var neutralActions = ActionValenceMapping.GetActionsForValence(Valence.Neutral);
            action = neutralActions[Random.Shared.Next(neutralActions.Length)];
            _logger?.LogDebug("Valence recalibration: selecting neutral action {Action}", action);
        }

        // Accumulate signal for ongoing valence tracking
        _valenceAuthorizer.AccumulateSignal(valenceSignal);

        // Add exploration bonus early in training (encourages trying different strategies)
        float explorationBonus = GetExplorationBonus();
        if (Random.Shared.NextDouble() < explorationBonus)
        {
            // Random exploration
            action = Random.Shared.Next(NeuralPolicy.NumActions);
        }

        var mode = NeuralPolicy.ActionToMode(action);
        var confidence = _policy.GetActionProbabilities(features)[action];

        // Adjust confidence based on valence alignment
        if (authorization.IsAuthorized && currentValenceState.IsStable)
        {
            confidence *= 1.1f; // Boost confidence when valence-aligned
        }
        else if (authorization.RequiresRecalibration)
        {
            confidence *= 0.7f; // Reduce confidence during recalibration
        }

        return (mode, Math.Clamp(confidence, 0f, 1f));
    }

    /// <summary>
    /// Calculate valence signal from current state and action.
    /// Positive signal → approach/engage behaviors favored
    /// Negative signal → avoid/retreat behaviors favored
    /// Near-zero signal → neutral/recalibration needed
    /// </summary>
    private static float CalculateValenceSignal(float[] features, int action)
    {
        float signal = 0f;

        // Health factor: low health → negative signal
        float health = features.Length > FeatureIndices.Health ? features[FeatureIndices.Health] : 1f;
        signal += (health - 0.5f) * 0.4f; // -0.2 to +0.2

        // Threat factor: many threats → negative signal
        float threats = features.Length > FeatureIndices.ThreatsInFov ? features[FeatureIndices.ThreatsInFov] : 0f;
        signal -= threats * 0.1f; // 0 to -0.5 (assuming max 5 threats)

        // Danger level: high danger → negative signal
        float danger = features.Length > FeatureIndices.DangerLevel ? features[FeatureIndices.DangerLevel] : 0f;
        signal -= danger * 0.3f; // 0 to -0.3

        // Ammo factor: good ammo → positive signal (can engage)
        float ammo = features.Length > FeatureIndices.AmmoClip ? features[FeatureIndices.AmmoClip] : 1f;
        signal += ammo * 0.2f; // 0 to +0.2

        // Target availability: has target → slightly positive (opportunity)
        float hasTarget = features.Length > FeatureIndices.HasTarget ? features[FeatureIndices.HasTarget] : 0f;
        signal += hasTarget * 0.1f; // 0 to +0.1

        return Math.Clamp(signal, -1f, 1f);
    }

    /// <summary>
    /// Called at the end of each step to record the outcome.
    /// Implements GAIN phase - observing and recording for knowledge discovery.
    /// Now includes VALENCE METABOLIZATION for experience processing.
    /// </summary>
    public void EndStep(GameState nextState, float reward, bool done)
    {
        if (!_isCollecting || _lastState == null)
            return;

        var nextFeatures = nextState.ToFeatureVector();

        // METABOLIZE: Process experience through valence system
        // This converts raw experience into valence-tagged learning signals
        var metabolized = _valenceMetabolizer.Metabolize(
            _lastState, _lastAction, reward, nextFeatures, done);

        // Log lessons learned from metabolization
        if (metabolized.LessonLearned != null && _session.TotalTimesteps % 100 == 0)
        {
            _logger?.LogDebug("Metabolized lesson: {Lesson} (valence={Valence})",
                metabolized.LessonLearned, metabolized.AssignedValence);
        }

        // Apply valence-based reward shaping
        // Positive valence experiences get a small bonus when outcome is good
        // Negative valence experiences get bonus for successfully avoiding harm
        float shapedReward = reward;
        if (metabolized.AssignedValence == Valence.Positive && reward > 0)
        {
            shapedReward *= 1.1f; // Reward success in positive mode
        }
        else if (metabolized.AssignedValence == Valence.Negative && reward >= 0)
        {
            shapedReward += 0.05f; // Small bonus for surviving in defensive mode
        }
        else if (metabolized.AssignedValence == Valence.Neutral)
        {
            // Neutral mode: encourage exploration but don't amplify rewards
            shapedReward *= 0.95f; // Slight penalty to encourage commitment to pos/neg
        }

        // Check for valence recalibration trigger
        // If in neutral too long or experiencing rapid valence swings, force recalibration
        var valenceState = _valenceAuthorizer.CurrentState;
        if (valenceState.IsRecalibrating && valenceState.Duration > 30)
        {
            // Been in neutral too long - need to pick a direction
            var recommended = _valenceMetabolizer.GetRecommendedValence(nextFeatures);
            if (recommended != Valence.Neutral)
            {
                float recalSignal = recommended == Valence.Positive ? 0.5f : -0.5f;
                _valenceAuthorizer.RequestTransition(recommended, recalSignal, 0.6f);
                _logger?.LogDebug("Recalibration complete: transitioning to {Valence}", recommended);
            }
        }

        // Create experience with shaped reward
        var experience = new Experience
        {
            State = _lastState,
            Action = _lastAction,
            Reward = shapedReward,
            NextState = nextFeatures,
            Done = done,
            LogProbability = _lastLogProb,
            Value = _lastValue
        };

        _currentTrajectory.Add(experience);
        _buffer.Add(experience);

        // GAIN: Feed observation to knowledge organizer
        // This enables the LOOP: observe → discover patterns → organize → apply
        // Include valence context in the observation
        var context = new Dictionary<string, float>
        {
            ["valence"] = (float)valenceState.Direction,
            ["valence_magnitude"] = valenceState.Magnitude,
            ["valence_confidence"] = valenceState.Confidence,
            ["metabolized_contribution"] = metabolized.ValenceContribution
        };
        _knowledge?.Observe(_lastState, _lastAction, reward, nextFeatures, done, context);

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

    public string GetDiagnostics()
    {
        var knowledgeInfo = _knowledge?.GetDiagnostics() ?? "Knowledge: disabled";
        var valenceState = _valenceAuthorizer.CurrentState;

        return $"""
            === SELF-TRAINER ===
            Timesteps: {_session.TotalTimesteps:N0}
            Episodes: {_session.TotalEpisodes:N0}
            Updates: {_session.TotalUpdates:N0}
            Mean Reward (100 ep): {MeanRecentReward:F2}
            Best Reward: {_session.BestMeanReward:F2}
            Mean Length (100 ep): {MeanRecentLength:F1}
            Exploration Bonus: {GetExplorationBonus():F3}
            Collecting: {_isCollecting}

            === VALENCE SYSTEM ===
            Current: {valenceState.Direction} (mag={valenceState.Magnitude:F2})
            Confidence: {valenceState.Confidence:F2}
            Duration: {valenceState.Duration} frames
            Net Signal: {valenceState.NetSignal:F2}
            Recalibrating: {valenceState.IsRecalibrating}
            Stable: {valenceState.IsStable}

            {_valenceAuthorizer.GetDiagnostics()}
            {_valenceMetabolizer.GetDiagnostics()}

            === KNOWLEDGE CYCLE ===
            GAIN:     {_knowledge?.TotalObservations ?? 0:N0} observations
            HOLD:     {_knowledge?.KnowledgeCount ?? 0} knowledge units
            CARRY:    via queries during BeginStep
            MULTIPLY: via promotion/demotion
            EMBODY:   via action overrides
            LOOP:     continuous discovery

            {_buffer.GetDiagnostics()}
            {_trainer.GetDiagnostics()}
            {_policy.GetDiagnostics()}
            {knowledgeInfo}
            ====================
            """;
    }

    public void Dispose()
    {
        _session.EndTime = DateTime.UtcNow;

        // Save final checkpoint
        if (_session.TotalUpdates > 0)
        {
            SaveCheckpoint("final");
        }

        // HOLD: Persist knowledge to disk
        _knowledge?.Save();
        _logger?.LogInformation("Knowledge base saved with {Count} units", _knowledge?.KnowledgeCount ?? 0);

        _trainer.Dispose();
        _policy.Dispose();
    }
}
