using System.Diagnostics;
using System.Runtime.InteropServices;
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
using OKET.Vision.Overlay;
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
    private readonly Win32Input _win32Input;
    private readonly SmoothMouseController _mouseController;

    // Logging
    private readonly EpisodeLogger _episodeLogger;
    private readonly PerformanceMonitor _perfMonitor;

    // Overlay
    private OverlayWindow? _overlayWindow;
    private readonly bool _enableOverlay;

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
            : new MotionDetector(); // Use motion detection to track any moving objects

        _policy = new RuleBasedPolicy();
        _skillExecutor = new SkillExecutor();
        _stateBuilder = new GameStateBuilder();
        _worldModel = new WorldModel();
        _safetyLayer = new SafetyLayer();

        _win32Input = new Win32Input();
        _inputController = _win32Input;
        _mouseController = new SmoothMouseController(_win32Input);

        _episodeLogger = new EpisodeLogger(config.LogDirectory);
        _perfMonitor = new PerformanceMonitor();
        _enableOverlay = config.EnableOverlay;
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

        // Initialize overlay and set input target window
        var targetWindow = FindGameWindow();
        if (targetWindow != IntPtr.Zero)
        {
            // Set input target window for focus management
            _win32Input.SetTargetWindow(targetWindow);
            _logger.LogInformation("Input target window set: {Handle:X}", targetWindow);

            if (_enableOverlay)
            {
                _overlayWindow = new OverlayWindow(targetWindow,
                    _frameSource.Resolution.Width,
                    _frameSource.Resolution.Height);
                _overlayWindow.Show();
                _logger.LogInformation("Debug overlay initialized");
            }
        }
        else
        {
            _logger.LogWarning("Could not find game window - input may not work correctly");
        }

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

        // Dispose overlay
        _overlayWindow?.Dispose();
        _overlayWindow = null;

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
            // Ensure game window is focused before sending input
            _win32Input.EnsureFocus();
            _inputController.Execute(safePlan);
        }

        var actuationTime = sw.ElapsedMilliseconds;
        _perfMonitor.RecordActuationTime(actuationTime);

        // 7. Update Overlay
        if (_overlayWindow != null)
        {
            var debugOverlay = _overlayWindow.DebugOverlay;

            // Update detections visualization - pass all detections
            debugOverlay.UpdateDetections(state.Detections);

            // Update debug state for panel display
            var outcome = CalculateOutcome(state);
            var debugState = new DebugState
            {
                IntentType = mode.ToString(),
                IntentReason = state.ThreatsInFov > 0
                    ? $"{state.ThreatsInFov} threats, {state.NearestThreatDistance:F0}px away"
                    : "No threats detected",
                Confidence = confidence,
                ActiveSkill = plan?.GetType().Name ?? "None",
                ChosenAction = plan?.ToString()?.Split('.').LastOrDefault() ?? "Idle",
                PredictionError = 0f,
                LastReward = outcome.Reward,
                ThreatCount = state.ThreatsInFov,
                Health = state.Hud.Health,
                Fps = _perfMonitor.CurrentFps
            };
            debugOverlay.UpdateDebugState(debugState);

            // Add markers for primary target
            if (state.Detections.PrimaryThreat != null)
            {
                var threat = state.Detections.PrimaryThreat;
                debugOverlay.AddMarker(threat.Box.Center, MarkerType.Target, "TARGET", 0.1f);
            }

            // Update navigation path visualization
            var navState = _skillExecutor.NavigationSkill.GetNavigationState();
            if (navState.HasPath && navState.CurrentPath != null)
            {
                debugOverlay.SetCurrentPath(navState.CurrentPath, navState.CurrentPathIndex);
            }

            // Update overlay position to track game window
            _overlayWindow.UpdatePosition();
        }

        // 8. Logging
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
        _overlayWindow?.Dispose();
        _frameSource.Dispose();
        _detector.Dispose();
        _inputController.Dispose();
        _episodeLogger.Dispose();
    }

    /// <summary>
    /// Find the game window handle.
    /// </summary>
    private static IntPtr FindGameWindow()
    {
        // Try common game window names
        string[] windowNames = { "Garry's Mod", "GMod", "hl2", "Source Engine" };

        foreach (var name in windowNames)
        {
            var hwnd = FindWindow(null, name);
            if (hwnd != IntPtr.Zero)
                return hwnd;
        }

        // Fallback: Find by class name
        var gmodHwnd = FindWindow("Valve001", null);
        if (gmodHwnd != IntPtr.Zero)
            return gmodHwnd;

        // Try to find the foreground window as last resort
        return GetForegroundWindow();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}

/// <summary>
/// Configuration for the agent.
/// </summary>
public sealed record AgentConfig
{
    public bool UseDxgiCapture { get; init; } = true;
    public bool UseNeuralDetector { get; init; } = false;
    public string? DetectorModelPath { get; init; }
    public bool EnableInput { get; init; } = true;
    public bool EnableLogging { get; init; } = true;
    public bool EnableOverlay { get; init; } = true;
    public string LogDirectory { get; init; } = "logs";
    public int TargetFps { get; init; } = 30;
}
