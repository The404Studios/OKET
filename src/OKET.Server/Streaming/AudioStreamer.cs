using NAudio.Wave;

namespace OKET.Server.Streaming;

/// <summary>
/// Captures and streams game audio for analysis.
/// Can detect audio cues like gunshots, footsteps, zombie sounds.
/// </summary>
public sealed class AudioStreamer : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private readonly Queue<AudioFrame> _frameBuffer = new();
    private readonly object _lock = new();
    private bool _disposed;
    private bool _isCapturing;

    private readonly int _bufferSize;
    private float _peakLevel;
    private float _averageLevel;

    /// <summary>Whether audio capture is active.</summary>
    public bool IsCapturing => _isCapturing;

    /// <summary>Current peak audio level [0, 1].</summary>
    public float PeakLevel => _peakLevel;

    /// <summary>Average audio level [0, 1].</summary>
    public float AverageLevel => _averageLevel;

    /// <summary>Event raised when audio data is available.</summary>
    public event EventHandler<AudioFrameEventArgs>? AudioFrameReady;

    /// <summary>Event raised when significant audio event detected.</summary>
    public event EventHandler<AudioEventArgs>? AudioEventDetected;

    public AudioStreamer(int bufferSize = 100)
    {
        _bufferSize = bufferSize;
    }

    /// <summary>
    /// Start capturing system audio.
    /// </summary>
    public void StartCapture()
    {
        if (_isCapturing) return;

        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
            _isCapturing = true;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to start audio capture: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Stop capturing audio.
    /// </summary>
    public void StopCapture()
    {
        if (!_isCapturing) return;

        _capture?.StopRecording();
        _isCapturing = false;
    }

    /// <summary>
    /// Get buffered audio frames.
    /// </summary>
    public AudioFrame[] GetBufferedFrames()
    {
        lock (_lock)
        {
            return _frameBuffer.ToArray();
        }
    }

    /// <summary>
    /// Get the latest audio frame.
    /// </summary>
    public AudioFrame? GetLatestFrame()
    {
        lock (_lock)
        {
            return _frameBuffer.Count > 0 ? _frameBuffer.Last() : null;
        }
    }

    /// <summary>
    /// Analyze current audio for game events.
    /// </summary>
    public AudioAnalysis Analyze()
    {
        var analysis = new AudioAnalysis
        {
            Timestamp = DateTime.UtcNow,
            PeakLevel = _peakLevel,
            AverageLevel = _averageLevel
        };

        // Detect audio events based on levels and patterns
        if (_peakLevel > 0.8f)
        {
            analysis.Events.Add(new DetectedAudioEvent
            {
                Type = AudioEventType.LoudNoise,
                Confidence = _peakLevel,
                Description = "Loud sound detected (possible gunshot)"
            });
        }

        if (_averageLevel > 0.3f && _averageLevel < 0.6f)
        {
            analysis.Events.Add(new DetectedAudioEvent
            {
                Type = AudioEventType.Ambient,
                Confidence = 0.5f,
                Description = "Ambient game sounds"
            });
        }

        return analysis;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        // Convert to float samples
        var buffer = new float[e.BytesRecorded / 4];
        Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);

        // Calculate levels
        float peak = 0f;
        float sum = 0f;

        for (int i = 0; i < buffer.Length; i++)
        {
            float abs = Math.Abs(buffer[i]);
            peak = Math.Max(peak, abs);
            sum += abs;
        }

        _peakLevel = peak;
        _averageLevel = sum / buffer.Length;

        // Create frame
        var frame = new AudioFrame
        {
            Timestamp = DateTime.UtcNow,
            SampleCount = buffer.Length,
            PeakLevel = peak,
            AverageLevel = _averageLevel,
            Samples = buffer
        };

        lock (_lock)
        {
            _frameBuffer.Enqueue(frame);
            while (_frameBuffer.Count > _bufferSize)
            {
                _frameBuffer.Dequeue();
            }
        }

        // Raise events
        AudioFrameReady?.Invoke(this, new AudioFrameEventArgs(frame));

        // Check for significant audio events
        if (peak > 0.7f)
        {
            var audioEvent = new AudioEvent
            {
                Timestamp = DateTime.UtcNow,
                Type = peak > 0.9f ? AudioEventType.Gunshot : AudioEventType.LoudNoise,
                Level = peak
            };

            AudioEventDetected?.Invoke(this, new AudioEventArgs(audioEvent));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _isCapturing = false;
        if (e.Exception != null)
        {
            // Log error but don't throw
            Console.WriteLine($"Audio capture error: {e.Exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopCapture();
        _capture?.Dispose();
        _capture = null;
    }
}

/// <summary>
/// Single frame of audio data.
/// </summary>
public sealed class AudioFrame
{
    public DateTime Timestamp { get; init; }
    public int SampleCount { get; init; }
    public float PeakLevel { get; init; }
    public float AverageLevel { get; init; }
    public float[] Samples { get; init; } = Array.Empty<float>();

    /// <summary>
    /// Get compact representation for streaming (without raw samples).
    /// </summary>
    public AudioFrameHeader ToHeader()
    {
        return new AudioFrameHeader
        {
            Timestamp = Timestamp,
            SampleCount = SampleCount,
            PeakLevel = PeakLevel,
            AverageLevel = AverageLevel
        };
    }
}

/// <summary>
/// Compact header without raw samples.
/// </summary>
public readonly struct AudioFrameHeader
{
    public DateTime Timestamp { get; init; }
    public int SampleCount { get; init; }
    public float PeakLevel { get; init; }
    public float AverageLevel { get; init; }
}

/// <summary>
/// Result of audio analysis.
/// </summary>
public sealed class AudioAnalysis
{
    public DateTime Timestamp { get; init; }
    public float PeakLevel { get; init; }
    public float AverageLevel { get; init; }
    public List<DetectedAudioEvent> Events { get; } = new();
}

/// <summary>
/// A detected audio event.
/// </summary>
public sealed class DetectedAudioEvent
{
    public AudioEventType Type { get; init; }
    public float Confidence { get; init; }
    public string Description { get; init; } = "";
}

/// <summary>
/// Types of audio events.
/// </summary>
public enum AudioEventType
{
    Unknown,
    Ambient,
    Footstep,
    Gunshot,
    Reload,
    ZombieSound,
    LoudNoise,
    Explosion,
    DoorOpen,
    PlayerVoice
}

/// <summary>
/// Audio event for event handler.
/// </summary>
public sealed class AudioEvent
{
    public DateTime Timestamp { get; init; }
    public AudioEventType Type { get; init; }
    public float Level { get; init; }
}

/// <summary>
/// Event args for audio frame ready.
/// </summary>
public sealed class AudioFrameEventArgs : EventArgs
{
    public AudioFrame Frame { get; }

    public AudioFrameEventArgs(AudioFrame frame)
    {
        Frame = frame;
    }
}

/// <summary>
/// Event args for audio event detected.
/// </summary>
public sealed class AudioEventArgs : EventArgs
{
    public AudioEvent Event { get; }

    public AudioEventArgs(AudioEvent audioEvent)
    {
        Event = audioEvent;
    }
}
