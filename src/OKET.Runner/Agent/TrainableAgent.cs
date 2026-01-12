using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OKET.Core.Types;
using OKET.Core.State;
using OKET.Core.Audio;
using OKET.Core.Actions;
using OKET.Core.Interfaces;
using OKET.Core.Cognition;
using OKET.Agent.State;
using OKET.Agent.Memory;
using OKET.Agent.Decision;
using OKET.Agent.Safety;
using OKET.Agent.Cognition;
using OKET.Agent.Learning;
using OKET.Vision.Hud;
using OKET.Vision.Detection;
using OKET.Vision.Capture;
using OKET.Vision.Audio;
using OKET.Input;
using OKET.Runner.Logging;

namespace OKET.Runner.Agent;

/// <summary>
/// Configuration for the trainable agent.
/// </summary>
public sealed record TrainableAgentConfig
{
    public bool UseDxgiCapture { get; init; } = true;
    public bool UseNeuralDetector { get; init; } = false;
    public string? DetectorModelPath { get; init; }
    public bool EnableInput { get; init; } = true;
    public bool EnableLogging { get; init; } = true;
    public string LogDirectory { get; init; } = "logs";
    public int TargetFps { get; init; } = 30;

    // Training settings
    public bool EnableTraining { get; init; } = true;
    public string ModelDirectory { get; init; } = "models";
    public bool LoadExistingModel { get; init; } = true;
    public int RolloutLength { get; init; } = 2048;
    public float LearningRate { get; init; } = 3e-4f;
}

/// <summary>
/// Self-training agent that learns from gameplay experience.
/// Combines cognitive processing with PPO-based reinforcement learning.
/// </summary>
public sealed class TrainableAgent : IDisposable
{
    private readonly ILogger<TrainableAgent> _logger;
    private readonly TrainableAgentConfig _config;

    // Perception
    private readonly IFrameSource _frameSource;
    private readonly IAudioSource _audioSource;
    private readonly IHudParser _hudParser;
    private readonly IObjectDetector _detector;

    // Cognition
    private readonly GameStateBuilder _stateBuilder;
    private readonly IWorldModel _worldModel;
    private readonly CognitiveController _cognitiveController;
    private readonly ISafetyLayer _safetyLayer;

    // Learning
    private readonly SelfTrainer _selfTrainer;
    private readonly NeuralPolicy _neuralPolicy;

    // Actuation
    private readonly IInputController _inputController;

    // Logging
    private readonly EpisodeLogger _episodeLogger;
    private readonly PerformanceMonitor _perfMonitor;

    // State
    private GameState? _lastState;
    private bool _isRunning;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _isRunning;
    public GameState? CurrentState => _lastState;
    public BeliefState? CurrentBelief => _cognitiveController.CommittedBelief;
    public InteroceptiveState? CurrentFeeling => _cognitiveController.CurrentFeeling;
    public PerformanceMonitor Performance => _perfMonitor;
    public SelfTrainer Trainer => _selfTrainer;
    public NeuralPolicy Policy => _neuralPolicy;

    public TrainableAgent(ILogger<TrainableAgent> logger, ILoggerFactory loggerFactory, TrainableAgentConfig config)
    {
        _logger = logger;
        _config = config;
        var selfTrainerLogger = loggerFactory.CreateLogger<SelfTrainer>();

        // Initialize perception
        _frameSource = config.UseDxgiCapture
            ? new DxgiFrameSource()
            : new WindowFrameSource();

        _audioSource = new WasapiAudioSource();
        _hudParser = new ZombieHudParser();
        _detector = config.UseNeuralDetector && !string.IsNullOrEmpty(config.DetectorModelPath)
            ? new OnnxObjectDetector()
            : new SimpleZombieDetector();

        // Initialize learning
        _neuralPolicy = new NeuralPolicy();
        var ppoConfig = new PPOConfig
        {
            LearningRate = config.LearningRate,
            RolloutLength = config.RolloutLength
        };
        var trainingConfig = new TrainingConfig
        {
            ModelDirectory = config.ModelDirectory,
            RolloutLength = config.RolloutLength
        };
        _selfTrainer = new SelfTrainer(_neuralPolicy, ppoConfig, trainingConfig, selfTrainerLogger);

        // Load existing model if available
        if (config.LoadExistingModel)
        {
            if (_selfTrainer.LoadBestModel())
            {
                _logger.LogInformation("Loaded existing trained model");
            }
            else
            {
                _logger.LogInformation("No existing model found, starting fresh");
            }
        }

        // Initialize cognition with neural policy
        _stateBuilder = new GameStateBuilder();
        _worldModel = new WorldModel();

        var skillExecutor = new SkillExecutor();
        _cognitiveController = new CognitiveController(_neuralPolicy, skillExecutor);

        _safetyLayer = new SafetyLayer();

        // Initialize actuation
        var win32Input = new Win32Input();
        _inputController = win32Input;

        // Initialize logging
        _episodeLogger = new EpisodeLogger(config.LogDirectory);
        _perfMonitor = new PerformanceMonitor();
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning) return;

        _logger.LogInformation("Starting Trainable Agent with self-learning...");

        // Start frame capture
        await _frameSource.StartAsync(ct);
        _logger.LogInformation("Frame capture: {Width}x{Height}",
            _frameSource.Resolution.Width, _frameSource.Resolution.Height);

        // Start audio capture
        try
        {
            await _audioSource.StartAsync(ct);
            _logger.LogInformation("Audio capture: {SampleRate}Hz, {Channels}ch",
                _audioSource.SampleRate, _audioSource.Channels);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio capture failed - running in vision-only mode");
        }

        // Configure HUD parser
        _hudParser.Configure(_frameSource.Resolution.Width, _frameSource.Resolution.Height);

        // Load detector model
        if (_detector is OnnxObjectDetector onnx && !string.IsNullOrEmpty(_config.DetectorModelPath))
        {
            await onnx.LoadAsync(_config.DetectorModelPath, ct);
        }

        if (_config.EnableLogging)
        {
            _episodeLogger.StartEpisode();
        }

        // Start training data collection
        if (_config.EnableTraining)
        {
            _selfTrainer.StartCollecting();
        }

        _perfMonitor.Start();
        _isRunning = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _logger.LogInformation("Trainable Agent started");
        _logger.LogInformation("Architecture: Vision + Audio → Fusion → Neural Policy → Skills → Actions → Learn");

        await RunLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;

        _logger.LogInformation("Stopping trainable agent...");

        _cts?.Cancel();
        _isRunning = false;
        _inputController.ReleaseAll();

        await _frameSource.StopAsync();
        await _audioSource.StopAsync();

        // Stop training and save final model
        _selfTrainer.StopCollecting();

        if (_config.EnableLogging)
        {
            _episodeLogger.EndEpisode();
        }

        _perfMonitor.Stop();
        _logger.LogInformation(_perfMonitor.GetSummary());
        _logger.LogInformation(_selfTrainer.GetDiagnostics());
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var targetFrameTime = TimeSpan.FromMilliseconds(1000.0 / _config.TargetFps);
        var frameStopwatch = new Stopwatch();

        while (!ct.IsCancellationRequested && _isRunning)
        {
            frameStopwatch.Restart();
            _perfMonitor.BeginFrame();

            try
            {
                await RunTrainableFrameAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in trainable frame");
            }

            _perfMonitor.EndFrame();

            var elapsed = frameStopwatch.Elapsed;
            if (elapsed < targetFrameTime)
            {
                await Task.Delay(targetFrameTime - elapsed, ct);
            }
        }
    }

    private async Task RunTrainableFrameAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // === PERCEPTION PHASE ===
        var frame = _frameSource.GetFrame();
        if (frame == null)
        {
            await Task.Delay(10, ct);
            return;
        }

        var hudState = _hudParser.Parse(frame);
        var audioSnapshot = _audioSource.GetSnapshot();

        // Apply perception modulation
        float perceptionMod = _cognitiveController.PerceptionModulation;
        _detector.ConfidenceThreshold = Math.Clamp(0.6f - (perceptionMod - 1f) * 0.15f, 0.3f, 0.8f);

        var detections = await _detector.DetectAsync(frame, ct);
        var perceptionTime = sw.ElapsedMilliseconds;
        _perfMonitor.RecordPerceptionTime(perceptionTime);
        sw.Restart();

        // === STATE BUILDING ===
        var gameState = _stateBuilder.Build(frame, hudState, detections, _lastState);
        _worldModel.Update(gameState);

        // === LEARNING-AUGMENTED DECISION ===
        // Get action from self-trainer (which uses neural policy with exploration)
        var (mode, confidence) = _selfTrainer.BeginStep(gameState);

        // Override cognitive controller's policy decision with learned decision
        // But still use cognitive processing for gating, feelings, etc.
        var plan = _cognitiveController.Process(gameState, audioSnapshot);

        // Blend learned mode with cognitive processing
        if (_config.EnableTraining)
        {
            // Use learned mode but keep cognitive modulation
            plan = plan with
            {
                Mode = mode,
                Confidence = confidence * plan.Confidence,
                Reason = plan.Reason + $" [learned:{mode}]"
            };
        }

        var decisionTime = sw.ElapsedMilliseconds;
        _perfMonitor.RecordDecisionTime(decisionTime);
        sw.Restart();

        // === SAFETY VALIDATION ===
        var safePlan = _safetyLayer.Validate(plan, gameState);

        // === ACTUATION ===
        if (_config.EnableInput)
        {
            _inputController.Execute(safePlan);
        }

        var actuationTime = sw.ElapsedMilliseconds;
        _perfMonitor.RecordActuationTime(actuationTime);

        // === REWARD CALCULATION & LEARNING ===
        if (_config.EnableTraining && _lastState != null)
        {
            var reward = CalculateReward(gameState, _lastState);
            bool done = gameState.Hud.IsDead;

            // Feed experience to trainer
            _selfTrainer.EndStep(gameState, reward, done);

            // Reset on death
            if (done && !_lastState.Hud.IsDead)
            {
                HandleDeath(gameState);
            }
        }

        // === LOGGING ===
        if (_config.EnableLogging)
        {
            var outcome = CalculateOutcome(gameState);
            _episodeLogger.LogStep(gameState, safePlan, outcome);
        }

        _lastState = gameState;

        // Periodic diagnostics
        if (_perfMonitor.TotalFrames % 300 == 0)
        {
            _logger.LogInformation(
                "Frame {Frame}: FPS={Fps:F1}, Mode={Mode}, Reward={Reward:F2}, Episodes={Ep}",
                _perfMonitor.TotalFrames,
                _perfMonitor.CurrentFps,
                safePlan.Mode,
                _selfTrainer.MeanRecentReward,
                _selfTrainer.Session.TotalEpisodes);
        }

        // Full diagnostics every 30 seconds
        if (_perfMonitor.TotalFrames % 900 == 0)
        {
            _logger.LogInformation(_selfTrainer.GetDiagnostics());
        }
    }

    /// <summary>
    /// Calculate reward signal for reinforcement learning.
    /// </summary>
    private float CalculateReward(GameState current, GameState previous)
    {
        float reward = 0f;

        // Survival bonus
        reward += 0.001f;

        // Damage dealt bonus
        var belief = _cognitiveController.CommittedBelief;
        if (belief?.HitConfirmed == true)
        {
            reward += 0.15f;
        }

        // Health-based penalties/rewards
        int healthDelta = current.Hud.Health - previous.Hud.Health;
        if (healthDelta < 0)
        {
            reward -= 0.05f * Math.Abs(healthDelta);
        }
        else if (healthDelta > 0)
        {
            reward += 0.02f * healthDelta; // Healing bonus
        }

        // Death penalty
        if (current.Hud.IsDead)
        {
            reward -= 1.0f;
        }

        // Wave progression bonus
        if (current.Hud.Wave > previous.Hud.Wave)
        {
            reward += 0.5f;
        }

        // Threat management
        if (current.ThreatsInFov < previous.ThreatsInFov && previous.ThreatsInFov > 0)
        {
            reward += 0.1f; // Reduced threats (killed or escaped)
        }

        // Ammo management (small penalty for running empty)
        if (current.Hud.AmmoClip == 0 && previous.Hud.AmmoClip > 0)
        {
            reward -= 0.02f;
        }

        // Stuck penalty
        if (current.IsStuck)
        {
            reward -= 0.01f;
        }

        return reward;
    }

    private void HandleDeath(GameState state)
    {
        _logger.LogInformation("Agent died at wave {Wave}. Restarting episode.", state.Hud.Wave);

        if (_config.EnableLogging)
        {
            var feeling = _cognitiveController.CurrentFeeling;
            _episodeLogger.LogEvent("death", new Dictionary<string, object>
            {
                ["wave"] = state.Hud.Wave,
                ["threats"] = state.ThreatsInFov,
                ["anxiety"] = feeling?.Anxiety ?? 0,
                ["mean_reward"] = _selfTrainer.MeanRecentReward
            });

            _episodeLogger.StartEpisode();
        }

        _worldModel.Reset();
        _cognitiveController.Reset();
    }

    private StepOutcome CalculateOutcome(GameState state)
    {
        var belief = _cognitiveController.CommittedBelief;
        int healthDelta = _lastState != null ? state.Hud.Health - _lastState.Hud.Health : 0;

        return new StepOutcome
        {
            Reward = _lastState != null ? CalculateReward(state, _lastState) : 0,
            GotHit = healthDelta < 0,
            DealtDamage = belief?.HitConfirmed ?? false,
            Died = state.Hud.IsDead,
            HealthDelta = healthDelta
        };
    }

    /// <summary>
    /// Train from previously logged episodes (offline training).
    /// </summary>
    public void TrainFromLogs(int numEpochs = 10)
    {
        _logger.LogInformation("Starting offline training from logs...");
        _selfTrainer.TrainFromLogs(_config.LogDirectory, numEpochs);
        _logger.LogInformation("Offline training complete");
    }

    /// <summary>
    /// Switch to evaluation mode (no exploration, no learning).
    /// </summary>
    public void SetEvaluationMode(bool eval = true)
    {
        _selfTrainer.SetEvaluationMode(eval);
        _logger.LogInformation("Evaluation mode: {Mode}", eval);
    }

    public void Dispose()
    {
        StopAsync().Wait();
        _frameSource.Dispose();
        _audioSource.Dispose();
        _detector.Dispose();
        _inputController.Dispose();
        _episodeLogger.Dispose();
        _selfTrainer.Dispose();
    }
}
