using System.Runtime.InteropServices;
using OKET.Core.Audio;
using OKET.Core.Interfaces;

namespace OKET.Vision.Audio;

/// <summary>
/// Audio capture using WASAPI loopback (captures what you hear).
/// </summary>
public sealed class WasapiAudioSource : IAudioSource
{
    private readonly IAudioClassifier _classifier;
    private readonly object _lock = new();

    private IntPtr _audioClient;
    private IntPtr _captureClient;
    private Thread? _captureThread;
    private bool _isCapturing;
    private CancellationTokenSource? _cts;

    // Circular buffer for samples
    private float[] _sampleBuffer = new float[48000 * 2]; // 2 seconds at 48kHz
    private int _writeIndex;
    private int _availableSamples;

    // Latest snapshot
    private AudioSnapshot _latestSnapshot = new() { IsValid = false };
    private readonly Queue<AudioEvent> _recentEvents = new();
    private const int MaxRecentEvents = 100;

    public bool IsCapturing => _isCapturing;
    public int SampleRate { get; private set; } = 48000;
    public int Channels { get; private set; } = 2;

    public WasapiAudioSource(IAudioClassifier? classifier = null)
    {
        _classifier = classifier ?? new TemplateAudioClassifier();
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_isCapturing) return;

                try
                {
                    InitializeWasapi();
                    _isCapturing = true;
                    _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    _captureThread = new Thread(CaptureLoop)
                    {
                        Name = "AudioCapture",
                        IsBackground = true,
                        Priority = ThreadPriority.AboveNormal
                    };
                    _captureThread.Start();
                }
                catch (Exception ex)
                {
                    _latestSnapshot = new AudioSnapshot
                    {
                        Timestamp = DateTime.UtcNow,
                        IsValid = false,
                        Events = []
                    };
                    throw new InvalidOperationException("Failed to initialize WASAPI audio capture", ex);
                }
            }
        }, ct);
    }

    public Task StopAsync()
    {
        lock (_lock)
        {
            if (!_isCapturing) return Task.CompletedTask;

            _cts?.Cancel();
            _captureThread?.Join(1000);
            _isCapturing = false;

            CleanupWasapi();
        }

        return Task.CompletedTask;
    }

    private void InitializeWasapi()
    {
        // Initialize COM
        CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);

        // Get default audio endpoint
        var hr = CoCreateInstance(
            ref CLSID_MMDeviceEnumerator,
            IntPtr.Zero,
            CLSCTX_ALL,
            ref IID_IMMDeviceEnumerator,
            out var enumerator);

        if (hr != 0)
            throw new COMException("Failed to create device enumerator", hr);

        try
        {
            // Get default render device (for loopback)
            hr = IMMDeviceEnumerator_GetDefaultAudioEndpoint(
                enumerator, eRender, eConsole, out var device);

            if (hr != 0)
                throw new COMException("Failed to get default audio endpoint", hr);

            try
            {
                // Activate audio client
                hr = IMMDevice_Activate(
                    device, ref IID_IAudioClient, CLSCTX_ALL, IntPtr.Zero, out _audioClient);

                if (hr != 0)
                    throw new COMException("Failed to activate audio client", hr);

                // Get mix format
                hr = IAudioClient_GetMixFormat(_audioClient, out var formatPtr);
                if (hr != 0)
                    throw new COMException("Failed to get mix format", hr);

                var format = Marshal.PtrToStructure<WAVEFORMATEX>(formatPtr);
                SampleRate = format.nSamplesPerSec;
                Channels = format.nChannels;

                // Resize buffer for actual sample rate
                _sampleBuffer = new float[SampleRate * 2];

                // Initialize in loopback mode
                hr = IAudioClient_Initialize(
                    _audioClient,
                    AUDCLNT_SHAREMODE_SHARED,
                    AUDCLNT_STREAMFLAGS_LOOPBACK,
                    10000000, // 1 second buffer
                    0,
                    formatPtr,
                    IntPtr.Zero);

                CoTaskMemFree(formatPtr);

                if (hr != 0)
                    throw new COMException("Failed to initialize audio client", hr);

                // Get capture client
                hr = IAudioClient_GetService(_audioClient, ref IID_IAudioCaptureClient, out _captureClient);
                if (hr != 0)
                    throw new COMException("Failed to get capture client", hr);

                // Start capturing
                hr = IAudioClient_Start(_audioClient);
                if (hr != 0)
                    throw new COMException("Failed to start audio client", hr);
            }
            finally
            {
                Marshal.Release(device);
            }
        }
        finally
        {
            Marshal.Release(enumerator);
        }
    }

    private void CleanupWasapi()
    {
        if (_audioClient != IntPtr.Zero)
        {
            IAudioClient_Stop(_audioClient);
            Marshal.Release(_audioClient);
            _audioClient = IntPtr.Zero;
        }

        if (_captureClient != IntPtr.Zero)
        {
            Marshal.Release(_captureClient);
            _captureClient = IntPtr.Zero;
        }
    }

    private void CaptureLoop()
    {
        var token = _cts!.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                CapturePackets();
                Thread.Sleep(10); // ~100Hz processing
            }
            catch (Exception)
            {
                // Continue on errors
            }
        }
    }

    private void CapturePackets()
    {
        var hr = IAudioCaptureClient_GetNextPacketSize(_captureClient, out var packetLength);
        if (hr != 0) return;

        while (packetLength > 0)
        {
            hr = IAudioCaptureClient_GetBuffer(
                _captureClient,
                out var dataPtr,
                out var framesAvailable,
                out var flags,
                out _,
                out _);

            if (hr != 0) break;

            try
            {
                // Convert to mono float and store in buffer
                ProcessSamples(dataPtr, framesAvailable, flags);
            }
            finally
            {
                IAudioCaptureClient_ReleaseBuffer(_captureClient, framesAvailable);
            }

            hr = IAudioCaptureClient_GetNextPacketSize(_captureClient, out packetLength);
            if (hr != 0) break;
        }

        // Process accumulated samples
        UpdateSnapshot();
    }

    private void ProcessSamples(IntPtr dataPtr, int frames, int flags)
    {
        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0)
        {
            // Silent buffer - write zeros
            lock (_lock)
            {
                for (int i = 0; i < frames; i++)
                {
                    _sampleBuffer[_writeIndex] = 0;
                    _writeIndex = (_writeIndex + 1) % _sampleBuffer.Length;
                }
                _availableSamples = Math.Min(_availableSamples + frames, _sampleBuffer.Length);
            }
            return;
        }

        // Read float samples (assuming 32-bit float format)
        var samples = new float[frames * Channels];
        Marshal.Copy(dataPtr, samples, 0, samples.Length);

        lock (_lock)
        {
            // Convert to mono and store
            for (int i = 0; i < frames; i++)
            {
                float mono = 0;
                for (int ch = 0; ch < Channels; ch++)
                {
                    mono += samples[i * Channels + ch];
                }
                mono /= Channels;

                _sampleBuffer[_writeIndex] = mono;
                _writeIndex = (_writeIndex + 1) % _sampleBuffer.Length;
            }
            _availableSamples = Math.Min(_availableSamples + frames, _sampleBuffer.Length);
        }
    }

    private void UpdateSnapshot()
    {
        var samples = GetSamples(SampleRate / 10); // 100ms of audio
        if (samples.Length == 0) return;

        // Calculate audio statistics
        float sum = 0, sumSq = 0, peak = 0;
        foreach (var s in samples)
        {
            var abs = Math.Abs(s);
            sum += abs;
            sumSq += s * s;
            peak = Math.Max(peak, abs);
        }

        float rms = MathF.Sqrt(sumSq / samples.Length);
        float avg = sum / samples.Length;

        // Classify events
        var events = _classifier.Classify(samples, SampleRate, DateTime.UtcNow);

        lock (_lock)
        {
            foreach (var evt in events)
            {
                _recentEvents.Enqueue(evt);
                while (_recentEvents.Count > MaxRecentEvents)
                    _recentEvents.Dequeue();
            }

            _latestSnapshot = new AudioSnapshot
            {
                Timestamp = DateTime.UtcNow,
                Events = _recentEvents.ToList(),
                AverageLevel = avg,
                PeakLevel = peak,
                SpectralCentroid = CalculateSpectralCentroid(samples),
                IsValid = true
            };
        }
    }

    private float CalculateSpectralCentroid(float[] samples)
    {
        // Simple FFT-less approximation using zero-crossing rate
        int crossings = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if ((samples[i] >= 0) != (samples[i - 1] >= 0))
                crossings++;
        }

        // Normalize to 0-1 range (higher = brighter sound)
        return Math.Clamp(crossings / (float)samples.Length * 10f, 0f, 1f);
    }

    public AudioSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return _latestSnapshot;
        }
    }

    public float[] GetSamples(int count)
    {
        lock (_lock)
        {
            count = Math.Min(count, _availableSamples);
            if (count <= 0) return [];

            var result = new float[count];
            int readIndex = (_writeIndex - count + _sampleBuffer.Length) % _sampleBuffer.Length;

            for (int i = 0; i < count; i++)
            {
                result[i] = _sampleBuffer[readIndex];
                readIndex = (readIndex + 1) % _sampleBuffer.Length;
            }

            return result;
        }
    }

    public void Dispose()
    {
        StopAsync().Wait();
    }

    #region COM Interop

    private const int COINIT_MULTITHREADED = 0;
    private const int CLSCTX_ALL = 23;
    private const int eRender = 0;
    private const int eConsole = 0;
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    private static Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, int dwClsContext,
        ref Guid riid, out IntPtr ppv);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("mmdevapi.dll", EntryPoint = "IMMDeviceEnumerator_GetDefaultAudioEndpoint")]
    private static extern int IMMDeviceEnumerator_GetDefaultAudioEndpoint(IntPtr self,
        int dataFlow, int role, out IntPtr ppEndpoint);

    [DllImport("mmdevapi.dll", EntryPoint = "IMMDevice_Activate")]
    private static extern int IMMDevice_Activate(IntPtr self, ref Guid iid, int dwClsCtx,
        IntPtr pActivationParams, out IntPtr ppInterface);

    [DllImport("audioclient.dll", EntryPoint = "IAudioClient_GetMixFormat")]
    private static extern int IAudioClient_GetMixFormat(IntPtr self, out IntPtr ppDeviceFormat);

    [DllImport("audioclient.dll", EntryPoint = "IAudioClient_Initialize")]
    private static extern int IAudioClient_Initialize(IntPtr self, int ShareMode, int StreamFlags,
        long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr AudioSessionGuid);

    [DllImport("audioclient.dll", EntryPoint = "IAudioClient_GetService")]
    private static extern int IAudioClient_GetService(IntPtr self, ref Guid riid, out IntPtr ppv);

    [DllImport("audioclient.dll", EntryPoint = "IAudioClient_Start")]
    private static extern int IAudioClient_Start(IntPtr self);

    [DllImport("audioclient.dll", EntryPoint = "IAudioClient_Stop")]
    private static extern int IAudioClient_Stop(IntPtr self);

    [DllImport("audioclient.dll", EntryPoint = "IAudioCaptureClient_GetNextPacketSize")]
    private static extern int IAudioCaptureClient_GetNextPacketSize(IntPtr self, out int pNumFramesInNextPacket);

    [DllImport("audioclient.dll", EntryPoint = "IAudioCaptureClient_GetBuffer")]
    private static extern int IAudioCaptureClient_GetBuffer(IntPtr self, out IntPtr ppData,
        out int pNumFramesToRead, out int pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);

    [DllImport("audioclient.dll", EntryPoint = "IAudioCaptureClient_ReleaseBuffer")]
    private static extern int IAudioCaptureClient_ReleaseBuffer(IntPtr self, int NumFramesRead);

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    #endregion
}
