using System.Text.Json;
using System.Text.Json.Serialization;
using OKET.Core.Telemetry;
using OKET.Core.Agent;

namespace OKET.Server.Replay;

/// <summary>
/// Replays telemetry from JSONL files for analysis and debugging.
///
/// Features:
/// - Tick-by-tick replay
/// - Analysis-only mode (no actions)
/// - Speed control
/// - Filtering by token type
/// </summary>
public sealed class TelemetryReplayer : IDisposable
{
    private readonly string _inputPath;
    private readonly StreamReader? _reader;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    private ReplayHeader? _header;
    private readonly Queue<ReplayRecord> _buffer = new();
    private long _currentTick;
    private bool _endOfFile;

    public string InputPath => _inputPath;
    public ReplayHeader? Header => _header;
    public long CurrentTick => _currentTick;
    public bool IsEndOfFile => _endOfFile;

    /// <summary>
    /// Event raised when a token is replayed.
    /// </summary>
    public event Action<ReplayedToken>? OnToken;

    /// <summary>
    /// Event raised when an action plan is replayed.
    /// </summary>
    public event Action<ReplayedActionPlan>? OnActionPlan;

    /// <summary>
    /// Event raised when an outcome is replayed.
    /// </summary>
    public event Action<ReplayedOutcome>? OnOutcome;

    /// <summary>
    /// Event raised when an agent state is replayed.
    /// </summary>
    public event Action<ReplayedAgentState>? OnAgentState;

    public TelemetryReplayer(string inputPath)
    {
        _inputPath = inputPath;

        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Replay file not found: {inputPath}");

        _reader = new StreamReader(inputPath);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        // Read header
        ReadHeader();
    }

    private void ReadHeader()
    {
        if (_reader == null) return;

        var line = _reader.ReadLine();
        if (string.IsNullOrEmpty(line)) return;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeElem) &&
                typeElem.GetString() == "header")
            {
                _header = new ReplayHeader
                {
                    SchemaVersion = root.GetProperty("schemaVersion").GetString() ?? "",
                    StartTime = root.GetProperty("startTime").GetDateTime(),
                    RunName = root.GetProperty("runName").GetString() ?? ""
                };
            }
        }
        catch
        {
            // Ignore header parsing errors
        }
    }

    /// <summary>
    /// Advance to the next tick and return all records for that tick.
    /// </summary>
    public IEnumerable<ReplayRecord> NextTick()
    {
        if (_endOfFile || _reader == null)
            yield break;

        // Determine target tick
        long targetTick = _currentTick + 1;

        // Read records until we hit a new tick
        while (!_endOfFile)
        {
            var record = ReadNextRecord();
            if (record == null)
            {
                _endOfFile = true;
                break;
            }

            if (record.TickId > targetTick)
            {
                // Buffer this record for next tick
                _buffer.Enqueue(record);
                break;
            }

            if (record.TickId == targetTick)
            {
                DispatchRecord(record);
                yield return record;
            }
        }

        // Also yield buffered records from this tick
        while (_buffer.Count > 0 && _buffer.Peek().TickId == targetTick)
        {
            var record = _buffer.Dequeue();
            DispatchRecord(record);
            yield return record;
        }

        _currentTick = targetTick;
    }

    /// <summary>
    /// Skip to a specific tick.
    /// </summary>
    public void SeekToTick(long tickId)
    {
        while (_currentTick < tickId && !_endOfFile)
        {
            foreach (var _ in NextTick()) { }
        }
    }

    /// <summary>
    /// Get all records (reads entire file).
    /// </summary>
    public IEnumerable<ReplayRecord> GetAllRecords()
    {
        while (!_endOfFile)
        {
            foreach (var record in NextTick())
            {
                yield return record;
            }
        }
    }

    /// <summary>
    /// Get statistics about the replay file.
    /// </summary>
    public ReplayStats GetStats()
    {
        var stats = new ReplayStats();
        var tempReader = new StreamReader(_inputPath);

        try
        {
            string? line;
            while ((line = tempReader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("type", out var typeElem))
                    {
                        var type = typeElem.GetString();
                        switch (type)
                        {
                            case "token": stats.TokenCount++; break;
                            case "action_plan": stats.ActionPlanCount++; break;
                            case "outcome": stats.OutcomeCount++; break;
                            case "agent_state": stats.AgentStateCount++; break;
                            case "prediction_error": stats.PredictionErrorCount++; break;
                            case "footer":
                                if (root.TryGetProperty("totalRecords", out var total))
                                    stats.TotalRecords = total.GetInt64();
                                if (root.TryGetProperty("durationSeconds", out var dur))
                                    stats.DurationSeconds = dur.GetDouble();
                                break;
                        }
                    }

                    if (root.TryGetProperty("tickId", out var tickElem))
                    {
                        var tick = tickElem.GetInt64();
                        stats.MinTick = Math.Min(stats.MinTick, tick);
                        stats.MaxTick = Math.Max(stats.MaxTick, tick);
                    }
                }
                catch { }
            }
        }
        finally
        {
            tempReader.Dispose();
        }

        return stats;
    }

    private ReplayRecord? ReadNextRecord()
    {
        if (_reader == null) return null;

        // First check buffer
        if (_buffer.Count > 0)
            return _buffer.Dequeue();

        while (true)
        {
            var line = _reader.ReadLine();
            if (line == null) return null;
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeElem))
                    continue;

                var type = typeElem.GetString();
                if (type == "header" || type == "footer")
                    continue;

                long tickId = 0;
                if (root.TryGetProperty("tickId", out var tickElem))
                    tickId = tickElem.GetInt64();

                return new ReplayRecord
                {
                    Type = type ?? "",
                    TickId = tickId,
                    RawJson = line
                };
            }
            catch
            {
                continue;
            }
        }
    }

    private void DispatchRecord(ReplayRecord record)
    {
        try
        {
            using var doc = JsonDocument.Parse(record.RawJson);
            var root = doc.RootElement;

            switch (record.Type)
            {
                case "token":
                    OnToken?.Invoke(ParseToken(root));
                    break;
                case "action_plan":
                    OnActionPlan?.Invoke(ParseActionPlan(root));
                    break;
                case "outcome":
                    OnOutcome?.Invoke(ParseOutcome(root));
                    break;
                case "agent_state":
                    OnAgentState?.Invoke(ParseAgentState(root));
                    break;
            }
        }
        catch { }
    }

    private static ReplayedToken ParseToken(JsonElement root)
    {
        return new ReplayedToken
        {
            TickId = root.GetProperty("tickId").GetInt64(),
            TimestampMs = root.GetProperty("timestampMs").GetInt64(),
            TokenType = root.GetProperty("tokenType").GetString() ?? "",
            Confidence = root.GetProperty("confidence").GetSingle()
        };
    }

    private static ReplayedActionPlan ParseActionPlan(JsonElement root)
    {
        return new ReplayedActionPlan
        {
            TickId = root.GetProperty("tickId").GetInt64(),
            IntentType = root.GetProperty("intentType").GetString() ?? "",
            IntentReason = root.TryGetProperty("intentReason", out var r) ? r.GetString() ?? "" : "",
            PolicyName = root.GetProperty("policyName").GetString() ?? "",
            Action = root.GetProperty("action").GetString() ?? "",
            Confidence = root.GetProperty("confidence").GetSingle()
        };
    }

    private static ReplayedOutcome ParseOutcome(JsonElement root)
    {
        return new ReplayedOutcome
        {
            TickId = root.GetProperty("tickId").GetInt64(),
            Action = root.GetProperty("action").GetString() ?? "",
            Success = root.GetProperty("success").GetBoolean(),
            Reward = root.GetProperty("reward").GetSingle()
        };
    }

    private static ReplayedAgentState ParseAgentState(JsonElement root)
    {
        return new ReplayedAgentState
        {
            TickId = root.GetProperty("tickId").GetInt64(),
            Intent = root.GetProperty("intent").GetString() ?? "",
            PolicyName = root.GetProperty("policyName").GetString() ?? "",
            Action = root.GetProperty("action").GetString() ?? "",
            Confidence = root.GetProperty("confidence").GetSingle(),
            Reward = root.GetProperty("reward").GetSingle(),
            ThreatCount = root.GetProperty("threatCount").GetInt32(),
            Health = root.GetProperty("health").GetInt32()
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reader?.Dispose();
    }
}

public sealed class ReplayHeader
{
    public string SchemaVersion { get; init; } = "";
    public DateTime StartTime { get; init; }
    public string RunName { get; init; } = "";
}

public sealed class ReplayRecord
{
    public string Type { get; init; } = "";
    public long TickId { get; init; }
    public string RawJson { get; init; } = "";
}

public sealed class ReplayStats
{
    public long TotalRecords { get; set; }
    public long TokenCount { get; set; }
    public long ActionPlanCount { get; set; }
    public long OutcomeCount { get; set; }
    public long AgentStateCount { get; set; }
    public long PredictionErrorCount { get; set; }
    public long MinTick { get; set; } = long.MaxValue;
    public long MaxTick { get; set; } = long.MinValue;
    public double DurationSeconds { get; set; }

    public long TickCount => MaxTick > MinTick ? MaxTick - MinTick + 1 : 0;
}

// Replayed record types
public sealed class ReplayedToken
{
    public long TickId { get; init; }
    public long TimestampMs { get; init; }
    public string TokenType { get; init; } = "";
    public float Confidence { get; init; }
}

public sealed class ReplayedActionPlan
{
    public long TickId { get; init; }
    public string IntentType { get; init; } = "";
    public string IntentReason { get; init; } = "";
    public string PolicyName { get; init; } = "";
    public string Action { get; init; } = "";
    public float Confidence { get; init; }
}

public sealed class ReplayedOutcome
{
    public long TickId { get; init; }
    public string Action { get; init; } = "";
    public bool Success { get; init; }
    public float Reward { get; init; }
}

public sealed class ReplayedAgentState
{
    public long TickId { get; init; }
    public string Intent { get; init; } = "";
    public string PolicyName { get; init; } = "";
    public string Action { get; init; } = "";
    public float Confidence { get; init; }
    public float Reward { get; init; }
    public int ThreatCount { get; init; }
    public int Health { get; init; }
}
