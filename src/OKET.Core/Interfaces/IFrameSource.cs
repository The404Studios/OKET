using OKET.Core.Types;

namespace OKET.Core.Interfaces;

/// <summary>
/// Source of frames from the game window.
/// </summary>
public interface IFrameSource : IDisposable
{
    /// <summary>Whether the source is currently capturing.</summary>
    bool IsCapturing { get; }

    /// <summary>Target window title/handle.</summary>
    string WindowTitle { get; }

    /// <summary>Start capturing frames.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stop capturing.</summary>
    Task StopAsync();

    /// <summary>Get the next frame. Returns null if not available.</summary>
    Frame? GetFrame();

    /// <summary>Current capture resolution.</summary>
    (int Width, int Height) Resolution { get; }

    /// <summary>Frames captured per second.</summary>
    float CurrentFps { get; }
}
