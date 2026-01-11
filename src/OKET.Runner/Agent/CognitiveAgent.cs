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
using OKET.Vision.Hud;
using OKET.Vision.Detection;
using OKET.Vision.Capture;
using OKET.Vision.Audio;
using OKET.Input;
using OKET.Runner.Logging;

namespace OKET.Runner.Agent;

/// <summary>
/// Cognitive agent with full multimodal perception and feeling layer.
/// Integrates vision, audio, belief fusion, and interoceptive processing.
/// </summary>
public sealed class CognitiveAgent : IDisposable
{
    private readonly ILogger<CognitiveAgent> _logger;
    private readonly AgentConfig _config;

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

    public CognitiveAgent(ILogger<CognitiveAgent> logger, AgentConfig config)
    {
        _logger = logger;
        _config = config;

        // Initialize perception
        _frameSource = config.UseDxgiCapture
            ? new DxgiFrameSource()
            : new WindowFrameSource();

        _audioSource = new WasapiAudioSource();
        _hudParser = new ZombieHudParser();
        _detector = config.UseNeuralDetector && !string.IsNullOrEmpty(config.DetectorModelPath)
            ? new OnnxObjectDetector()
            : new SimpleZombieDetector();

        // Initialize cognition
        _stateBuilder = new GameStateBuilder();
        _worldModel = new WorldModel();

        var policy = new RuleBasedPolicy();
        var skillExecutor = new SkillExecutor();
        _cognitiveController = new CognitiveController(policy, skillExecutor);

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

        _logger.LogInformation("Starting Cognitive Agent with multimodal perception...");

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

        _perfMonitor.Start();
        _isRunning = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _logger.LogInformation("Cognitive Agent started");
        _logger.LogInformation("Architecture: Vision + Audio → Fusion → Belief → Feeling → Decision");

        await RunLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;

        _logger.LogInformation("Stopping agent...");

        _cts?.Cancel();
        _isRunning = false;
        _inputController.ReleaseAll();

        await _frameSource.StopAsync();
        await _audioSource.StopAsync();

        if (_config.EnableLogging)
        {
            _episodeLogger.EndEpisode();
        }

        _perfMonitor.Stop();
        _logger.LogInformation(_perfMonitor.GetSummary());
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
                await RunCognitiveFrameAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cognitive frame");
            }

            _perfMonitor.EndFrame();

            var elapsed = frameStopwatch.Elapsed;
            if (elapsed < targetFrameTime)
            {
                await Task.Delay(targetFrameTime - elapsed, ct);
            }
        }
    }

    private async Task RunCognitiveFrameAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // === PERCEPTION PHASE ===

        // Get frame
        var frame = _frameSource.GetFrame();
        if (frame == null)
        {
            await Task.Delay(10, ct);
            return;
        }

        // Parse HUD
        var hudState = _hudParser.Parse(frame);

        // Get audio snapshot
        var audioSnapshot = _audioSource.GetSnapshot();

        // Apply perception modulation to detector threshold
        // Low modulation = be conservative (higher threshold)
        // High modulation = be aggressive (lower threshold)
        float perceptionMod = _cognitiveController.PerceptionModulation;
        _detector.ConfidenceThreshold = Math.Clamp(0.6f - (perceptionMod - 1f) * 0.15f, 0.3f, 0.8f);

        // Run object detection
        var detections = await _detector.DetectAsync(frame, ct);

        var perceptionTime = sw.ElapsedMilliseconds;
        _perfMonitor.RecordPerceptionTime(perceptionTime);
        sw.Restart();

        // === STATE BUILDING ===

        // Build game state from perceptions
        var gameState = _stateBuilder.Build(frame, hudState, detections, _lastState);

        // Update world model (tracking)
        _worldModel.Update(gameState);

        // === COGNITIVE PROCESSING ===

        // Run full cognitive pipeline:
        // Fusion → Z-Scores → Interoception → Decision
        var plan = _cognitiveController.Process(gameState, audioSnapshot);

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

        // === LOGGING ===

        if (_config.EnableLogging)
        {
            var outcome = CalculateOutcome(gameState);
            _episodeLogger.LogStep(gameState, safePlan, outcome);

            // Log death events
            if (gameState.Hud.IsDead && (_lastState == null || !_lastState.Hud.IsDead))
            {
                var feeling = _cognitiveController.CurrentFeeling;
                _episodeLogger.LogEvent("death", new Dictionary<string, object>
                {
                    ["wave"] = gameState.Hud.Wave,
                    ["threats"] = gameState.ThreatsInFov,
                    ["anxiety"] = feeling?.Anxiety ?? 0,
                    ["control"] = feeling?.ControlConfidence ?? 0
                });

                _episodeLogger.StartEpisode();
                _worldModel.Reset();
            }
        }

        _lastState = gameState;

        // Periodic diagnostics
        if (_perfMonitor.TotalFrames % 150 == 0)
        {
            var belief = _cognitiveController.CommittedBelief;
            var feeling = _cognitiveController.CurrentFeeling;

            _logger.LogDebug(
                "Frame {Frame}: FPS={Fps:F1}, Mode={Mode}, Threat={Threat:F2}, " +
                "Feeling={Feeling}, Stability={Stability:F2}",
                _perfMonitor.TotalFrames,
                _perfMonitor.CurrentFps,
                safePlan.Mode,
                belief?.ThreatLevel ?? 0,
                GetDominantFeeling(feeling),
                feeling?.GlobalStability ?? 0);
        }

        // Full diagnostics every 10 seconds
        if (_perfMonitor.TotalFrames % 300 == 0)
        {
            _logger.LogInformation(_cognitiveController.GetDiagnostics());
        }
    }

    private string GetDominantFeeling(InteroceptiveState? feeling)
    {
        if (feeling == null) return "NONE";

        return (feeling.Anxiety, feeling.Frustration, feeling.Focus, feeling.Vigilance) switch
        {
            var (a, _, _, _) when a > 0.6f => "ANXIOUS",
            var (_, f, _, _) when f > 0.6f => "FRUSTRATED",
            var (_, _, fo, _) when fo > 0.6f => "FOCUSED",
            var (_, _, _, v) when v > 0.6f => "VIGILANT",
            _ => "NEUTRAL"
        };
    }

    private StepOutcome CalculateOutcome(GameState state)
    {
        var belief = _cognitiveController.CommittedBelief;
        int healthDelta = _lastState != null ? state.Hud.Health - _lastState.Hud.Health : 0;

        float reward = 0f;
        if (belief?.HitConfirmed == true) reward += 0.15f;
        if (healthDelta < 0) reward -= 0.05f * Math.Abs(healthDelta);
        if (state.Hud.IsDead) reward -= 1f;
        reward += 0.001f; // Survival bonus

        return new StepOutcome
        {
            Reward = reward,
            GotHit = healthDelta < 0,
            DealtDamage = belief?.HitConfirmed ?? false,
            Died = state.Hud.IsDead,
            HealthDelta = healthDelta
        };
    }

    public void Dispose()
    {
        StopAsync().Wait();
        _frameSource.Dispose();
        _audioSource.Dispose();
        _detector.Dispose();
        _inputController.Dispose();
        _episodeLogger.Dispose();
    }
}
