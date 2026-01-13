using System.Text.Json;
using System.Text.Json.Serialization;
using OKET.Core.Telemetry;
using OKET.Core.Agent;

namespace OKET.Server.Replay;

/// <summary>
/// Records telemetry to JSONL format for replay and analysis.
///
/// File format: runs/{date}_{time}_{name}.jsonl
/// Each line is a JSON object representing one telemetry event.
/// </summary>
public sealed class TelemetryRecorder : IDisposable
{
    private readonly string _outputPath;
    private readonly StreamWriter? _writer;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;
    private long _recordCount;
    private DateTime _startTime;

    public string OutputPath => _outputPath;
    public long RecordCount => _recordCount;
    public bool IsRecording => _writer != null && !_disposed;

    public TelemetryRecorder(string outputDirectory, string runName = "run")
    {
        Directory.CreateDirectory(outputDirectory);

        _startTime = DateTime.UtcNow;
        var timestamp = _startTime.ToString("yyyy-MM-dd_HHmm");
        var fileName = $"{timestamp}_{runName}.jsonl";
        _outputPath = Path.Combine(outputDirectory, fileName);

        _writer = new StreamWriter(_outputPath, append: false);

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        // Write header record
        WriteRecord(new RecordHeader
        {
            Type = "header",
            SchemaVersion = $"{TelemetrySchema.VersionMajor}.{TelemetrySchema.VersionMinor}",
            StartTime = _startTime,
            RunName = runName
        });
    }

    /// <summary>
    /// Record a telemetry token.
    /// </summary>
    public void RecordToken(TelemetryToken token)
    {
        if (_disposed || _writer == null) return;

        var record = new TokenRecord
        {
            Type = "token",
            TickId = token.Header.TickId,
            TimestampMs = token.Header.TimestampUnixMs,
            TokenType = token.Header.Type.ToString(),
            Confidence = token.Header.Confidence,
            Payload = SerializePayload(token.Payload)
        };

        WriteRecord(record);
    }

    /// <summary>
    /// Record an action plan.
    /// </summary>
    public void RecordActionPlan(ActionPlan plan)
    {
        if (_disposed || _writer == null) return;

        var record = new ActionPlanRecord
        {
            Type = "action_plan",
            TickId = plan.TickId,
            IntentType = plan.Intent.Type.ToString(),
            IntentPriority = plan.Intent.Priority,
            IntentUrgency = plan.Intent.Urgency,
            IntentReason = plan.Intent.Reason,
            PolicyName = plan.PolicyName,
            Action = plan.Action.ToString(),
            ParamA = plan.ParamA,
            ParamB = plan.ParamB,
            Confidence = plan.Confidence
        };

        WriteRecord(record);
    }

    /// <summary>
    /// Record an action outcome.
    /// </summary>
    public void RecordOutcome(ActionOutcome outcome)
    {
        if (_disposed || _writer == null) return;

        var record = new OutcomeRecord
        {
            Type = "outcome",
            TickId = outcome.TickId,
            Action = outcome.Action.ToString(),
            Success = outcome.Success,
            Reward = outcome.Reward,
            FailureReason = outcome.FailureReason
        };

        WriteRecord(record);
    }

    /// <summary>
    /// Record a prediction error measurement.
    /// </summary>
    public void RecordPredictionError(long tickId, PredictionTarget target, int trackId, float error)
    {
        if (_disposed || _writer == null) return;

        var record = new PredictionErrorRecord
        {
            Type = "prediction_error",
            TickId = tickId,
            Target = target.ToString(),
            TrackId = trackId,
            ErrorMagnitude = error
        };

        WriteRecord(record);
    }

    /// <summary>
    /// Record agent state snapshot.
    /// </summary>
    public void RecordAgentState(AgentStateSnapshot state)
    {
        if (_disposed || _writer == null) return;

        var record = new AgentStateRecord
        {
            Type = "agent_state",
            TickId = state.TickId,
            Intent = state.Intent.ToString(),
            IntentReason = state.IntentReason,
            PolicyName = state.PolicyName,
            ActiveSkill = state.ActiveSkill,
            Action = state.Action.ToString(),
            Confidence = state.PolicyConfidence,
            Reward = state.LastReward,
            PredictionError = state.PredictionError,
            ThreatCount = state.ThreatCount,
            Health = state.Health
        };

        WriteRecord(record);
    }

    private void WriteRecord(object record)
    {
        if (_writer == null) return;

        try
        {
            var json = JsonSerializer.Serialize(record, _jsonOptions);
            _writer.WriteLine(json);
            _recordCount++;

            // Flush periodically for safety
            if (_recordCount % 100 == 0)
            {
                _writer.Flush();
            }
        }
        catch (Exception)
        {
            // Silently ignore write errors to not affect game performance
        }
    }

    private static Dictionary<string, object?> SerializePayload(ITokenPayload payload)
    {
        return payload switch
        {
            EntityToken e => new Dictionary<string, object?>
            {
                ["kind"] = e.Kind.ToString(),
                ["trackId"] = e.TrackId,
                ["x"] = e.X,
                ["y"] = e.Y,
                ["w"] = e.W,
                ["h"] = e.H,
                ["distanceM"] = e.DistanceM,
                ["vx"] = e.Vx,
                ["vy"] = e.Vy
            },
            SelfStateToken s => new Dictionary<string, object?>
            {
                ["health"] = s.Health01,
                ["armor"] = s.Armor01,
                ["ammo"] = s.Ammo01,
                ["isReloading"] = s.IsReloading,
                ["isDead"] = s.IsDead,
                ["wave"] = s.WaveNumber
            },
            AudioCueToken a => new Dictionary<string, object?>
            {
                ["cueType"] = a.CueType.ToString(),
                ["direction"] = a.DirectionDeg,
                ["volume"] = a.Volume01,
                ["distance"] = a.DistanceHint
            },
            UiTextToken u => new Dictionary<string, object?>
            {
                ["text"] = u.Text,
                ["x"] = u.X,
                ["y"] = u.Y,
                ["size"] = u.SizeHint
            },
            NavigationToken n => new Dictionary<string, object?>
            {
                ["targetX"] = n.TargetX,
                ["targetY"] = n.TargetY,
                ["distance"] = n.DistanceToTarget,
                ["blocked"] = n.IsBlocked,
                ["progress"] = n.PathProgress01
            },
            PredictionToken p => new Dictionary<string, object?>
            {
                ["target"] = p.Target.ToString(),
                ["trackId"] = p.TrackId,
                ["predA"] = p.PredA,
                ["predB"] = p.PredB,
                ["horizon"] = p.HorizonSeconds
            },
            ErrorToken err => new Dictionary<string, object?>
            {
                ["target"] = err.Target.ToString(),
                ["trackId"] = err.TrackId,
                ["magnitude"] = err.ErrorMagnitude,
                ["errorA"] = err.ErrorA,
                ["errorB"] = err.ErrorB
            },
            _ => new Dictionary<string, object?> { ["raw"] = payload.ToString() }
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Write footer record
        WriteRecord(new RecordFooter
        {
            Type = "footer",
            EndTime = DateTime.UtcNow,
            TotalRecords = _recordCount,
            DurationSeconds = (DateTime.UtcNow - _startTime).TotalSeconds
        });

        _writer?.Flush();
        _writer?.Dispose();
    }
}

// Record types for JSON serialization
internal sealed class RecordHeader
{
    public string Type { get; init; } = "";
    public string SchemaVersion { get; init; } = "";
    public DateTime StartTime { get; init; }
    public string RunName { get; init; } = "";
}

internal sealed class RecordFooter
{
    public string Type { get; init; } = "";
    public DateTime EndTime { get; init; }
    public long TotalRecords { get; init; }
    public double DurationSeconds { get; init; }
}

internal sealed class TokenRecord
{
    public string Type { get; init; } = "";
    public long TickId { get; init; }
    public long TimestampMs { get; init; }
    public string TokenType { get; init; } = "";
    public float Confidence { get; init; }
    public Dictionary<string, object?>? Payload { get; init; }
}

internal sealed class ActionPlanRecord
{
    public string Type { get; init; } = "";
    public long TickId { get; init; }
    public string IntentType { get; init; } = "";
    public float IntentPriority { get; init; }
    public float IntentUrgency { get; init; }
    public string IntentReason { get; init; } = "";
    public string PolicyName { get; init; } = "";
    public string Action { get; init; } = "";
    public float ParamA { get; init; }
    public float ParamB { get; init; }
    public float Confidence { get; init; }
}

internal sealed class OutcomeRecord
{
    public string Type { get; init; } = "";
    public long TickId { get; init; }
    public string Action { get; init; } = "";
    public bool Success { get; init; }
    public float Reward { get; init; }
    public string? FailureReason { get; init; }
}

internal sealed class PredictionErrorRecord
{
    public string Type { get; init; } = "";
    public long TickId { get; init; }
    public string Target { get; init; } = "";
    public int TrackId { get; init; }
    public float ErrorMagnitude { get; init; }
}

internal sealed class AgentStateRecord
{
    public string Type { get; init; } = "";
    public long TickId { get; init; }
    public string Intent { get; init; } = "";
    public string IntentReason { get; init; } = "";
    public string PolicyName { get; init; } = "";
    public string ActiveSkill { get; init; } = "";
    public string Action { get; init; } = "";
    public float Confidence { get; init; }
    public float Reward { get; init; }
    public float PredictionError { get; init; }
    public int ThreatCount { get; init; }
    public int Health { get; init; }
}
