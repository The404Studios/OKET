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
            var config = ParseArgs(args);

            // Build host
            var host = Host.CreateDefaultBuilder(args)
                .UseSerilog()
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton(config);
                    services.AddSingleton<ZombieSurvivalAgent>();
                })
                .Build();

            // Run agent
            using var agent = host.Services.GetRequiredService<ZombieSurvivalAgent>();

            // Handle Ctrl+C
            Console.CancelKeyPress += async (s, e) =>
            {
                e.Cancel = true;
                Log.Information("Shutdown requested...");
                await agent.StopAsync();
            };

            Log.Information("Starting agent with config:");
            Log.Information("  DXGI Capture: {UseDxgi}", config.UseDxgiCapture);
            Log.Information("  Neural Detector: {UseNeural}", config.UseNeuralDetector);
            Log.Information("  Input Enabled: {EnableInput}", config.EnableInput);
            Log.Information("  Logging Enabled: {EnableLogging}", config.EnableLogging);
            Log.Information("  Target FPS: {Fps}", config.TargetFps);
            Log.Information("");
            Log.Information("Press Ctrl+C to stop.");
            Log.Information("");

            await agent.StartAsync();

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

    static AgentConfig ParseArgs(string[] args)
    {
        var config = new AgentConfig();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLower())
            {
                case "--no-input":
                    config = config with { EnableInput = false };
                    break;

                case "--no-logging":
                    config = config with { EnableLogging = false };
                    break;

                case "--gdi-capture":
                    config = config with { UseDxgiCapture = false };
                    break;

                case "--neural":
                    config = config with { UseNeuralDetector = true };
                    break;

                case "--model":
                    if (i + 1 < args.Length)
                    {
                        config = config with { DetectorModelPath = args[++i] };
                    }
                    break;

                case "--fps":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var fps))
                    {
                        config = config with { TargetFps = fps };
                    }
                    break;

                case "--log-dir":
                    if (i + 1 < args.Length)
                    {
                        config = config with { LogDirectory = args[++i] };
                    }
                    break;

                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }

        return config;
    }

    static void PrintHelp()
    {
        Console.WriteLine("""
            OKET - GMod Zombie Survival AI Agent

            Usage: OKET.Runner [options]

            Options:
              --no-input       Disable input (observation only mode)
              --no-logging     Disable episode logging
              --gdi-capture    Use GDI capture instead of DXGI
              --neural         Use neural network detector (requires model)
              --model <path>   Path to ONNX detector model
              --fps <n>        Target frames per second (default: 30)
              --log-dir <dir>  Directory for logs (default: logs)
              --help, -h       Show this help

            Controls:
              Ctrl+C           Stop the agent

            Example:
              OKET.Runner --fps 60 --neural --model models/zombie_detector.onnx
            """);
    }
}
