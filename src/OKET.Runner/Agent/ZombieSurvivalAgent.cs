using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OKET.Core.Types;
using OKET.Core.State;
using OKET.Core.Actions;
using OKET.Core.Interfaces;
using OKET.Agent.State;
using OKET.Agent.Memory;
using OKET.Agent.Decision;
using OKET.Agent.Safety;
using OKET.Vision.Hud;
using OKET.Vision.Detection;
using OKET.Vision.Capture;
using OKET.Input;
using OKET.Runner.Logging;

namespace OKET.Runner.Agent;

/// <summary>
/// Main agent orchestrator for Zombie Survival gameplay.
/// Coordinates perception, decision, and action execution.
/// </summary>
public sealed class ZombieSurvivalAgent : IDisposable
{
    private readonly ILogger<ZombieSurvivalAgent> _logger;
    private readonly AgentConfig _config;

    // Perception
    private readonly IFrameSource _frameSource;
    private readonly IHudParser _hudParser;
    private readonly IObjectDetector _detector;

    // Decision
    private readonly IPolicy _policy;
    private readonly SkillExecutor _skillExecutor;
    private readonly IStateBuilder _stateBuilder;
    private readonly IWorldModel _worldModel;
    private readonly ISafetyLayer _safetyLayer;

    // Actuation
    private readonly IInputController _inputController;
    private readonly SmoothMouseController _mouseController;

    // Logging
    private readonly EpisodeLogger _episodeLogger;
    private readonly PerformanceMonitor _perfMonitor;

    // State
    private GameState? _lastState;
    private bool _isRunning;
    private CancellationTokenSource? _cts;
    private int _lastHealth = 100;

    public bool IsRunning => _isRunning;
    public GameState? CurrentState => _lastState;
    public PerformanceMonitor Performance => _perfMonitor;

    public ZombieSurvivalAgent(ILogger<ZombieSurvivalAgent> logger, AgentConfig config)
    {
        _logger = logger;
        _config = config;

        // Initialize components
        _frameSource = config.UseDxgiCapture
            ? new DxgiFrameSource()
            : new WindowFrameSource();

        _hudParser = new ZombieHudParser();
        _detector = config.UseNeuralDetector && !string.IsNullOrEmpty(config.DetectorModelPath)
            ? new OnnxObjectDetector()
            : new SimpleZombieDetector();

        _policy = new RuleBasedPolicy();
        _skillExecutor = new SkillExecutor();
        _stateBuilder = new GameStateBuilder();
        _worldModel = new WorldModel();
        _safetyLayer = new SafetyLayer();

        var win32Input = new Win32Input();
        _inputController = win32Input;
        _mouseController = new SmoothMouseController(win32Input);

        _episodeLogger = new EpisodeLogger(config.LogDirectory);
        _perfMonitor = new PerformanceMonitor();
    }

    /// <summary>
    /// Start the agent.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning) return;

        _logger.LogInformation("Starting Zombie Survival Agent...");

        // Initialize frame source
        await _frameSource.StartAsync(ct);
        _logger.LogInformation("Frame capture started: {Width}x{Height}",
            _frameSource.Resolution.Width, _frameSource.Resolution.Height);

        // Configure HUD parser for resolution
        _hudParser.Configure(_frameSource.Resolution.Width, _frameSource.Resolution.Height);

        // Load detector model if using neural detector
        if (_detector is OnnxObjectDetector onnxDetector && !string.IsNullOrEmpty(_config.DetectorModelPath))
        {
            _logger.LogInformation("Loading detector model: {Path}", _config.DetectorModelPath);
            await onnxDetector.LoadAsync(_config.DetectorModelPath, ct);
        }

        // Start episode logging
        if (_config.EnableLogging)
        {
            _episodeLogger.StartEpisode();
        }

        _perfMonitor.Start();
        _isRunning = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _logger.LogInformation("Agent started successfully");

        // Run main loop
        await RunLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Stop the agent.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning) return;

        _logger.LogInformation("Stopping agent...");

        _cts?.Cancel();
        _isRunning = false;

        // Release all inputs
        _inputController.ReleaseAll();

        // Stop frame capture
        await _frameSource.StopAsync();

        // End episode
        if (_config.EnableLogging)
        {
            _episodeLogger.EndEpisode();
        }

        _perfMonitor.Stop();

        _logger.LogInformation("Agent stopped. {Summary}", _perfMonitor.GetSummary());
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
                await RunFrameAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in agent frame");
            }

            _perfMonitor.EndFrame();

            // Frame rate limiting
            var elapsed = frameStopwatch.Elapsed;
            if (elapsed < targetFrameTime)
            {
                await Task.Delay(targetFrameTime - elapsed, ct);
            }
        }
    }

    private async Task RunFrameAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // 1. Perception: Capture frame and extract information
        var frame = _frameSource.GetFrame();
        if (frame == null)
        {
            await Task.Delay(10, ct);
            return;
        }

        // Parse HUD
        var hudState = _hudParser.Parse(frame);

        // Run object detection
        var detections = await _detector.DetectAsync(frame, ct);

        var perceptionTime = sw.ElapsedMilliseconds;
        _perfMonitor.RecordPerceptionTime(perceptionTime);
        sw.Restart();

        // 2. State Building: Convert percepts to state
        var state = _stateBuilder.Build(frame, hudState, detections, _lastState);

        // 3. World Model Update: Track targets
        _worldModel.Update(state);

        // 4. Decision: Determine what to do
        var (mode, confidence) = _policy.Decide(state);
        var plan = _skillExecutor.Execute(state, mode);

        var decisionTime = sw.ElapsedMilliseconds;
        _perfMonitor.RecordDecisionTime(decisionTime);
        sw.Restart();

        // 5. Safety: Validate plan
        var safePlan = _safetyLayer.Validate(plan, state);

        // 6. Actuation: Execute plan
        if (_config.EnableInput)
        {
            _inputController.Execute(safePlan);
        }

        var actuationTime = sw.ElapsedMilliseconds;
        _perfMonitor.RecordActuationTime(actuationTime);

        // 7. Logging
        if (_config.EnableLogging)
        {
            var outcome = CalculateOutcome(state);
            _episodeLogger.LogStep(state, safePlan, outcome);

            // Log significant events
            if (state.Hud.IsDead && (_lastState == null || !_lastState.Hud.IsDead))
            {
                _episodeLogger.LogEvent("death", new Dictionary<string, object>
                {
                    ["wave"] = state.Hud.Wave,
                    ["threatCount"] = state.ThreatsInFov
                });

                // Start new episode on death
                _episodeLogger.StartEpisode();
                _worldModel.Reset();
            }
        }

        // Update tracking
        _lastHealth = state.Hud.Health;
        _lastState = state;

        // Periodic logging
        if (_perfMonitor.TotalFrames % 300 == 0) // Every ~10 seconds at 30fps
        {
            _logger.LogDebug(
                "Frame {Frame}: FPS={Fps:F1}, Mode={Mode}, Threats={Threats}, Health={Health}",
                _perfMonitor.TotalFrames,
                _perfMonitor.CurrentFps,
                mode,
                state.ThreatsInFov,
                state.Hud.Health);
        }
    }

    private StepOutcome CalculateOutcome(GameState state)
    {
        int healthDelta = state.Hud.Health - _lastHealth;
        bool gotHit = healthDelta < 0;
        bool dealtDamage = state.Aim.HitConfirmed;

        // Simple reward signal
        float reward = 0f;

        // Reward for dealing damage
        if (dealtDamage) reward += 0.1f;

        // Penalty for taking damage
        if (gotHit) reward -= 0.05f * Math.Abs(healthDelta);

        // Penalty for death
        if (state.Hud.IsDead) reward -= 1.0f;

        // Small reward for staying alive
        reward += 0.001f;

        return new StepOutcome
        {
            Reward = reward,
            GotHit = gotHit,
            DealtDamage = dealtDamage,
            Died = state.Hud.IsDead,
            HealthDelta = healthDelta
        };
    }

    public void Dispose()
    {
        StopAsync().Wait();
        _frameSource.Dispose();
        _detector.Dispose();
        _inputController.Dispose();
        _episodeLogger.Dispose();
    }
}

/// <summary>
/// Configuration for the agent.
/// </summary>
public sealed class AgentConfig
{
    public bool UseDxgiCapture { get; init; } = true;
    public bool UseNeuralDetector { get; init; } = false;
    public string? DetectorModelPath { get; init; }
    public bool EnableInput { get; init; } = true;
    public bool EnableLogging { get; init; } = true;
    public string LogDirectory { get; init; } = "logs";
    public int TargetFps { get; init; } = 30;
}
