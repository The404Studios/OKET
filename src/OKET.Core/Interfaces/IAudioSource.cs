using OKET.Core.Audio;

namespace OKET.Core.Interfaces;

/// <summary>
/// Source of audio data from the game.
/// </summary>
public interface IAudioSource : IDisposable
{
    /// <summary>Whether audio capture is active.</summary>
    bool IsCapturing { get; }

    /// <summary>Sample rate in Hz.</summary>
    int SampleRate { get; }

    /// <summary>Number of channels.</summary>
    int Channels { get; }

    /// <summary>Start capturing audio.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stop capturing audio.</summary>
    Task StopAsync();

    /// <summary>Get the latest audio snapshot.</summary>
    AudioSnapshot GetSnapshot();

    /// <summary>Get raw samples for analysis (mono, normalized).</summary>
    float[] GetSamples(int count);
}

/// <summary>
/// Classifies audio into discrete events.
/// </summary>
public interface IAudioClassifier
{
    /// <summary>Process audio samples and detect events.</summary>
    IReadOnlyList<AudioEvent> Classify(float[] samples, int sampleRate, DateTime timestamp);

    /// <summary>Minimum confidence threshold.</summary>
    float ConfidenceThreshold { get; set; }
}
