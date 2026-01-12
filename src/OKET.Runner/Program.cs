using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using OKET.Runner.Agent;

namespace OKET.Runner;

class Program
{
    static async Task<int> Main(string[] args)
    {
        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File("logs/agent-.log",
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            Log.Information("OKET - GMod Zombie Survival AI Agent");
            Log.Information("=====================================");

            // Parse arguments
            var (config, trainConfig, mode) = ParseArgs(args);

            // Build host
            var host = Host.CreateDefaultBuilder(args)
                .UseSerilog()
                .ConfigureServices((context, services) =>
                {
                    if (mode == RunMode.Train || mode == RunMode.TrainAndPlay)
                    {
                        services.AddSingleton(trainConfig);
                        services.AddSingleton<TrainableAgent>();
                    }
                    else
                    {
                        services.AddSingleton(config);
                        services.AddSingleton<ZombieSurvivalAgent>();
                    }
                })
                .Build();

            // Run based on mode
            switch (mode)
            {
                case RunMode.Train:
                case RunMode.TrainAndPlay:
                    await RunTrainableAgent(host, trainConfig, mode);
                    break;

                case RunMode.OfflineTrain:
                    await RunOfflineTraining(host, trainConfig);
                    break;

                case RunMode.Evaluate:
                    await RunEvaluation(host, trainConfig);
                    break;

                default:
                    await RunRuleBasedAgent(host, config);
                    break;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Agent terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    static async Task RunRuleBasedAgent(IHost host, AgentConfig config)
    {
        using var agent = host.Services.GetRequiredService<ZombieSurvivalAgent>();

        Console.CancelKeyPress += async (s, e) =>
        {
            e.Cancel = true;
            Log.Information("Shutdown requested...");
            await agent.StopAsync();
        };

        Log.Information("Starting RULE-BASED agent:");
        Log.Information("  DXGI Capture: {UseDxgi}", config.UseDxgiCapture);
        Log.Information("  Neural Detector: {UseNeural}", config.UseNeuralDetector);
        Log.Information("  Input Enabled: {EnableInput}", config.EnableInput);
        Log.Information("  Target FPS: {Fps}", config.TargetFps);
        Log.Information("");
        Log.Information("Press Ctrl+C to stop.");
        Log.Information("");

        await agent.StartAsync();
    }

    static async Task RunTrainableAgent(IHost host, TrainableAgentConfig config, RunMode mode)
    {
        using var agent = host.Services.GetRequiredService<TrainableAgent>();

        Console.CancelKeyPress += async (s, e) =>
        {
            e.Cancel = true;
            Log.Information("Shutdown requested - saving model...");
            await agent.StopAsync();
        };

        Log.Information("Starting SELF-TRAINING agent:");
        Log.Information("  Mode: {Mode}", mode);
        Log.Information("  DXGI Capture: {UseDxgi}", config.UseDxgiCapture);
        Log.Information("  Input Enabled: {EnableInput}", config.EnableInput);
        Log.Information("  Model Directory: {ModelDir}", config.ModelDirectory);
        Log.Information("  Learning Rate: {LR}", config.LearningRate);
        Log.Information("  Rollout Length: {Rollout}", config.RolloutLength);
        Log.Information("");
        Log.Information("The agent will learn from gameplay experience.");
        Log.Information("Models are auto-saved to {Dir}", config.ModelDirectory);
        Log.Information("");
        Log.Information("Press Ctrl+C to stop and save.");
        Log.Information("");

        await agent.StartAsync();
    }

    static async Task RunOfflineTraining(IHost host, TrainableAgentConfig config)
    {
        Log.Information("Starting OFFLINE TRAINING from logged episodes...");
        Log.Information("  Log Directory: {LogDir}", config.LogDirectory);
        Log.Information("  Model Directory: {ModelDir}", config.ModelDirectory);
        Log.Information("");

        using var agent = host.Services.GetRequiredService<TrainableAgent>();
        agent.TrainFromLogs(numEpochs: 50);

        Log.Information("Offline training complete!");
        Log.Information(agent.Trainer.GetDiagnostics());
    }

    static async Task RunEvaluation(IHost host, TrainableAgentConfig config)
    {
        using var agent = host.Services.GetRequiredService<TrainableAgent>();
        agent.SetEvaluationMode(true);

        Console.CancelKeyPress += async (s, e) =>
        {
            e.Cancel = true;
            Log.Information("Shutdown requested...");
            await agent.StopAsync();
        };

        Log.Information("Starting EVALUATION mode (no learning):");
        Log.Information("  Model Directory: {ModelDir}", config.ModelDirectory);
        Log.Information("  Input Enabled: {EnableInput}", config.EnableInput);
        Log.Information("");
        Log.Information("Running trained policy without exploration.");
        Log.Information("Press Ctrl+C to stop.");
        Log.Information("");

        await agent.StartAsync();
    }

    static (AgentConfig config, TrainableAgentConfig trainConfig, RunMode mode) ParseArgs(string[] args)
    {
        var config = new AgentConfig();
        var trainConfig = new TrainableAgentConfig();
        var mode = RunMode.RuleBased;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLower())
            {
                // Mode selection
                case "--train":
                    mode = RunMode.Train;
                    trainConfig = trainConfig with { EnableTraining = true };
                    break;

                case "--train-play":
                    mode = RunMode.TrainAndPlay;
                    trainConfig = trainConfig with { EnableTraining = true, EnableInput = true };
                    break;

                case "--offline-train":
                    mode = RunMode.OfflineTrain;
                    break;

                case "--eval":
                case "--evaluate":
                    mode = RunMode.Evaluate;
                    trainConfig = trainConfig with { EnableTraining = false };
                    break;

                // Common options
                case "--no-input":
                    config = config with { EnableInput = false };
                    trainConfig = trainConfig with { EnableInput = false };
                    break;

                case "--no-logging":
                    config = config with { EnableLogging = false };
                    trainConfig = trainConfig with { EnableLogging = false };
                    break;

                case "--gdi-capture":
                    config = config with { UseDxgiCapture = false };
                    trainConfig = trainConfig with { UseDxgiCapture = false };
                    break;

                case "--neural":
                    config = config with { UseNeuralDetector = true };
                    trainConfig = trainConfig with { UseNeuralDetector = true };
                    break;

                case "--model":
                    if (i + 1 < args.Length)
                    {
                        var modelPath = args[++i];
                        config = config with { DetectorModelPath = modelPath };
                        trainConfig = trainConfig with { DetectorModelPath = modelPath };
                    }
                    break;

                case "--fps":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var fps))
                    {
                        config = config with { TargetFps = fps };
                        trainConfig = trainConfig with { TargetFps = fps };
                    }
                    break;

                case "--log-dir":
                    if (i + 1 < args.Length)
                    {
                        var logDir = args[++i];
                        config = config with { LogDirectory = logDir };
                        trainConfig = trainConfig with { LogDirectory = logDir };
                    }
                    break;

                // Training-specific options
                case "--model-dir":
                    if (i + 1 < args.Length)
                    {
                        trainConfig = trainConfig with { ModelDirectory = args[++i] };
                    }
                    break;

                case "--lr":
                case "--learning-rate":
                    if (i + 1 < args.Length && float.TryParse(args[++i], out var lr))
                    {
                        trainConfig = trainConfig with { LearningRate = lr };
                    }
                    break;

                case "--rollout":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var rollout))
                    {
                        trainConfig = trainConfig with { RolloutLength = rollout };
                    }
                    break;

                case "--no-load":
                    trainConfig = trainConfig with { LoadExistingModel = false };
                    break;

                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }

        return (config, trainConfig, mode);
    }

    static void PrintHelp()
    {
        Console.WriteLine("""
            OKET - GMod Zombie Survival AI Agent with Self-Training

            Usage: OKET.Runner [mode] [options]

            MODES:
              (default)        Run with rule-based policy (no learning)
              --train          Train while observing (no input to game)
              --train-play     Train while actually playing the game
              --offline-train  Train from previously logged episodes
              --eval           Run trained model without exploration

            COMMON OPTIONS:
              --no-input       Disable input (observation only)
              --no-logging     Disable episode logging
              --gdi-capture    Use GDI capture instead of DXGI
              --neural         Use neural network detector
              --model <path>   Path to ONNX detector model
              --fps <n>        Target frames per second (default: 30)
              --log-dir <dir>  Directory for logs (default: logs)
              --help, -h       Show this help

            TRAINING OPTIONS:
              --model-dir <dir>    Directory for trained models (default: models)
              --lr <float>         Learning rate (default: 0.0003)
              --rollout <n>        Rollout length for PPO (default: 2048)
              --no-load            Don't load existing model, start fresh

            EXAMPLES:
              # Rule-based agent (original behavior)
              OKET.Runner

              # Train by watching gameplay (no input)
              OKET.Runner --train --no-input

              # Train while playing
              OKET.Runner --train-play

              # Train from logged data
              OKET.Runner --offline-train --log-dir logs

              # Evaluate trained model
              OKET.Runner --eval --model-dir models

            The agent uses PPO (Proximal Policy Optimization) to learn
            from gameplay experience. Models are automatically saved
            when performance improves.
            """);
    }
}

enum RunMode
{
    RuleBased,
    Train,
    TrainAndPlay,
    OfflineTrain,
    Evaluate
}
