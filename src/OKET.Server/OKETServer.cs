using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OKET.Core.State;
using OKET.Server.Tokenization;
using OKET.Server.Accuracy;
using OKET.Server.Streaming;

namespace OKET.Server;

/// <summary>
/// OKET Server - handles tokenization, accuracy tracking, and data streaming.
/// Provides a TCP interface for external tools and analysis.
/// </summary>
public sealed class OKETServer : IDisposable
{
    private readonly GameStateTokenizer _tokenizer;
    private readonly AccuracyTracker _accuracy;
    private readonly AudioStreamer _audio;

    private TcpListener? _listener;
    private readonly List<TcpClient> _clients = new();
    private readonly object _clientLock = new();

    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool _disposed;

    /// <summary>Whether the server is running.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Server port.</summary>
    public int Port { get; }

    /// <summary>Connected client count.</summary>
    public int ClientCount
    {
        get
        {
            lock (_clientLock)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>Access to tokenizer.</summary>
    public GameStateTokenizer Tokenizer => _tokenizer;

    /// <summary>Access to accuracy tracker.</summary>
    public AccuracyTracker Accuracy => _accuracy;

    /// <summary>Access to audio streamer.</summary>
    public AudioStreamer Audio => _audio;

    /// <summary>Event raised when state is tokenized.</summary>
    public event EventHandler<TokenizedEventArgs>? StateTokenized;

    public OKETServer(int port = 9876)
    {
        Port = port;
        _tokenizer = new GameStateTokenizer();
        _accuracy = new AccuracyTracker();
        _audio = new AudioStreamer();
    }

    /// <summary>
    /// Start the server.
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        IsRunning = true;

        _acceptTask = AcceptClientsAsync(_cts.Token);

        // Start audio capture
        try
        {
            _audio.StartCapture();
        }
        catch
        {
            // Audio capture may fail on some systems, continue without it
        }
    }

    /// <summary>
    /// Stop the server.
    /// </summary>
    public void Stop()
    {
        if (!IsRunning) return;

        _cts?.Cancel();
        _listener?.Stop();
        _audio.StopCapture();

        lock (_clientLock)
        {
            foreach (var client in _clients)
            {
                client.Close();
            }
            _clients.Clear();
        }

        IsRunning = false;
    }

    /// <summary>
    /// Process and stream a game state.
    /// </summary>
    public GameStateToken ProcessState(GameState state)
    {
        // Tokenize
        var token = _tokenizer.Tokenize(state);

        // Stream to connected clients
        BroadcastToken(token);

        // Raise event
        StateTokenized?.Invoke(this, new TokenizedEventArgs(token));

        return token;
    }

    /// <summary>
    /// Process state and evaluate prediction accuracy.
    /// </summary>
    public (GameStateToken Token, AccuracyReport? Report) ProcessStateWithPrediction(
        GameState state, Core.Prediction.FramePrediction? prediction)
    {
        var token = ProcessState(state);
        AccuracyReport? report = null;

        if (prediction != null)
        {
            // Build actual positions dictionary
            var actualPositions = new Dictionary<int, Core.Types.Vector2>();
            foreach (var detection in state.Detections.Detections)
            {
                actualPositions[detection.TrackId] = detection.Box.Center;
            }

            report = _accuracy.EvaluatePrediction(
                prediction,
                actualPositions,
                state.ThreatsInFov,
                state.NearestThreatDistance);

            // Broadcast accuracy update
            BroadcastAccuracy();
        }

        return (token, report);
    }

    /// <summary>
    /// Get server status.
    /// </summary>
    public ServerStatus GetStatus()
    {
        return new ServerStatus
        {
            IsRunning = IsRunning,
            Port = Port,
            ClientCount = ClientCount,
            AudioCapturing = _audio.IsCapturing,
            AudioPeakLevel = _audio.PeakLevel,
            Accuracy = _accuracy.GetSummary(),
            Timestamp = DateTime.UtcNow
        };
    }

    private async Task AcceptClientsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);

                lock (_clientLock)
                {
                    _clients.Add(client);
                }

                // Send welcome message
                _ = SendToClientAsync(client, new ServerMessage
                {
                    Type = "welcome",
                    Data = JsonSerializer.Serialize(GetStatus())
                });

                // Start reading from client
                _ = ReadFromClientAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Accept failed, continue
            }
        }
    }

    private async Task ReadFromClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];

            while (!ct.IsCancellationRequested && client.Connected)
            {
                int read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;

                var message = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                await HandleClientMessage(client, message);
            }
        }
        catch
        {
            // Client disconnected
        }
        finally
        {
            lock (_clientLock)
            {
                _clients.Remove(client);
            }
            client.Close();
        }
    }

    private async Task HandleClientMessage(TcpClient client, string message)
    {
        try
        {
            var request = JsonSerializer.Deserialize<ClientRequest>(message);
            if (request == null) return;

            switch (request.Command?.ToLower())
            {
                case "status":
                    await SendToClientAsync(client, new ServerMessage
                    {
                        Type = "status",
                        Data = JsonSerializer.Serialize(GetStatus())
                    });
                    break;

                case "accuracy":
                    await SendToClientAsync(client, new ServerMessage
                    {
                        Type = "accuracy",
                        Data = JsonSerializer.Serialize(_accuracy.GetSummary())
                    });
                    break;

                case "reset":
                    _accuracy.Reset();
                    await SendToClientAsync(client, new ServerMessage
                    {
                        Type = "reset",
                        Data = "Accuracy reset"
                    });
                    break;
            }
        }
        catch
        {
            // Invalid message format
        }
    }

    private void BroadcastToken(GameStateToken token)
    {
        var message = new ServerMessage
        {
            Type = "token",
            Data = _tokenizer.ToJson(token)
        };

        BroadcastMessage(message);
    }

    private void BroadcastAccuracy()
    {
        var message = new ServerMessage
        {
            Type = "accuracy",
            Data = JsonSerializer.Serialize(_accuracy.GetSummary())
        };

        BroadcastMessage(message);
    }

    private void BroadcastMessage(ServerMessage message)
    {
        var json = JsonSerializer.Serialize(message) + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);

        lock (_clientLock)
        {
            foreach (var client in _clients.ToList())
            {
                try
                {
                    if (client.Connected)
                    {
                        client.GetStream().Write(bytes);
                    }
                }
                catch
                {
                    // Client disconnected
                    _clients.Remove(client);
                }
            }
        }
    }

    private async Task SendToClientAsync(TcpClient client, ServerMessage message)
    {
        try
        {
            var json = JsonSerializer.Serialize(message) + "\n";
            var bytes = Encoding.UTF8.GetBytes(json);
            await client.GetStream().WriteAsync(bytes);
        }
        catch
        {
            // Send failed
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _audio.Dispose();
    }
}

/// <summary>
/// Message sent from server to clients.
/// </summary>
public sealed class ServerMessage
{
    public string Type { get; init; } = "";
    public string Data { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Request from client to server.
/// </summary>
public sealed class ClientRequest
{
    public string? Command { get; init; }
    public string? Data { get; init; }
}

/// <summary>
/// Server status information.
/// </summary>
public sealed class ServerStatus
{
    public bool IsRunning { get; init; }
    public int Port { get; init; }
    public int ClientCount { get; init; }
    public bool AudioCapturing { get; init; }
    public float AudioPeakLevel { get; init; }
    public AccuracySummary? Accuracy { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Event args for state tokenized.
/// </summary>
public sealed class TokenizedEventArgs : EventArgs
{
    public GameStateToken Token { get; }

    public TokenizedEventArgs(GameStateToken token)
    {
        Token = token;
    }
}
