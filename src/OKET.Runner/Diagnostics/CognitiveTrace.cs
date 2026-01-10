using System.Text;
using OKET.Core.Cognition;
using OKET.Core.Actions;
using OKET.Agent.Cognition;

namespace OKET.Runner.Diagnostics;

/// <summary>
/// Live trace of cognitive state for debugging and validation.
/// Tracks Z-scores, feeling knobs, and BeliefLock state over time.
/// </summary>
public sealed class CognitiveTrace
{
    private readonly Queue<TraceEntry> _history = new();
    private readonly object _lock = new();
    private const int MaxHistory = 300; // 10 seconds at 30fps

    // Timing validation
    private readonly Queue<(DateTime Time, float Value)> _z0Samples = new();
    private readonly Queue<(DateTime Time, float Value)> _z1Samples = new();
    private readonly Queue<(DateTime Time, float Value)> _z4Samples = new();
    private const int TimingSampleCount = 90; // 3 seconds

    /// <summary>
    /// Record a trace entry.
    /// </summary>
    public void Record(
        ZScoreStack zScores,
        InteroceptiveState feeling,
        CommittedState commitment,
        StrategicMode executedMode)
    {
        lock (_lock)
        {
            var entry = new TraceEntry
            {
                Timestamp = DateTime.UtcNow,
                FrameNumber = _history.Count,

                // Z-scores
                Z0_VisionMotion = zScores.Z0.Vision_Motion,
                Z0_VisionThreats = zScores.Z0.Vision_ThreatCount,
                Z0_AudioLevel = zScores.Z0.Audio_Level,
                Z0_AudioThreats = zScores.Z0.Audio_ThreatSounds,
                Z1_Agreement = zScores.Z1_PerceptualAgreement,
                Z2_Stability = zScores.Z2_BeliefStability,
                Z3_Control = zScores.Z3_ControlEfficacy,
                Z4_Coherence = zScores.Z4_GlobalCoherence,
                SystemStrain = zScores.SystemStrain,

                // Feeling
                PredictionError = feeling.PredictionError,
                ThreatPressure = feeling.ThreatPressure,
                ControlConfidence = feeling.ControlConfidence,
                SensoryAlignment = feeling.SensoryAlignment,
                OutcomeTrend = feeling.OutcomeTrend,
                GlobalStability = feeling.GlobalStability,

                // Control knobs
                PerceptionTrust = feeling.PerceptionTrust,
                CommitmentConf = feeling.CommitmentConfidence,
                ActionSpeed = feeling.ActionSpeedModifier,
                LearningRate = feeling.LearningRateModifier,
                ShouldHesitate = feeling.ShouldHesitate,
                MustActNow = feeling.MustActNow,

                // Emotional labels (for logging only)
                Anxiety = feeling.Anxiety,
                Frustration = feeling.Frustration,
                Focus = feeling.Focus,
                Vigilance = feeling.Vigilance,

                // BeliefLock
                CommittedMode = commitment.Mode,
                ExecutedMode = executedMode,
                IsLocked = commitment.IsLocked,
                FramesSinceCommit = commitment.FramesSinceCommit,
                HasCandidate = commitment.HasCandidate,
                CandidateFrames = commitment.CandidateFrames,
                ForcedUnlock = commitment.ForcedUnlock,
                StrainTrend = commitment.StrainTrend,
                OutcomeTrendLock = commitment.OutcomeTrend,
                CommitReason = commitment.CommitReason
            };

            _history.Enqueue(entry);
            while (_history.Count > MaxHistory)
                _history.Dequeue();

            // Track timing samples
            var now = DateTime.UtcNow;
            _z0Samples.Enqueue((now, Math.Abs(entry.Z0_VisionMotion) + Math.Abs(entry.Z0_AudioLevel)));
            _z1Samples.Enqueue((now, entry.Z1_Agreement));
            _z4Samples.Enqueue((now, entry.Z4_Coherence));

            while (_z0Samples.Count > TimingSampleCount) _z0Samples.Dequeue();
            while (_z1Samples.Count > TimingSampleCount) _z1Samples.Dequeue();
            while (_z4Samples.Count > TimingSampleCount) _z4Samples.Dequeue();
        }
    }

    /// <summary>
    /// Get live ASCII visualization of Z-scores.
    /// </summary>
    public string GetZScoreDisplay()
    {
        lock (_lock)
        {
            if (_history.Count == 0) return "No data";

            var latest = _history.Last();
            var sb = new StringBuilder();

            sb.AppendLine("┌─────────────────────────────────────────────────┐");
            sb.AppendLine("│ Z-SCORE STACK                                   │");
            sb.AppendLine("├─────────────────────────────────────────────────┤");
            sb.AppendLine($"│ Z₀ Vision  {Bar(latest.Z0_VisionMotion, -3, 3)} {latest.Z0_VisionMotion,6:F2} │");
            sb.AppendLine($"│ Z₀ Audio   {Bar(latest.Z0_AudioLevel, -3, 3)} {latest.Z0_AudioLevel,6:F2} │");
            sb.AppendLine($"│ Z₁ Agree   {Bar(latest.Z1_Agreement, -3, 3)} {latest.Z1_Agreement,6:F2} │");
            sb.AppendLine($"│ Z₂ Stable  {Bar(latest.Z2_Stability, -3, 3)} {latest.Z2_Stability,6:F2} │");
            sb.AppendLine($"│ Z₃ Control {Bar(latest.Z3_Control, -3, 3)} {latest.Z3_Control,6:F2} │");
            sb.AppendLine($"│ Z₄ Coher   {Bar(latest.Z4_Coherence, -3, 3)} {latest.Z4_Coherence,6:F2} │");
            sb.AppendLine("├─────────────────────────────────────────────────┤");
            sb.AppendLine($"│ STRAIN     {Bar(latest.SystemStrain, 0, 3)} {latest.SystemStrain,6:F2} │");
            sb.AppendLine("└─────────────────────────────────────────────────┘");

            return sb.ToString();
        }
    }

    /// <summary>
    /// Get live ASCII visualization of feeling state.
    /// </summary>
    public string GetFeelingDisplay()
    {
        lock (_lock)
        {
            if (_history.Count == 0) return "No data";

            var latest = _history.Last();
            var sb = new StringBuilder();

            // Determine dominant feeling
            var dominant = (latest.Anxiety, latest.Frustration, latest.Focus, latest.Vigilance) switch
            {
                var (a, _, _, _) when a > 0.6f => $"ANXIOUS ({a:F2})",
                var (_, f, _, _) when f > 0.6f => $"FRUSTRATED ({f:F2})",
                var (_, _, fo, _) when fo > 0.6f => $"FOCUSED ({fo:F2})",
                var (_, _, _, v) when v > 0.6f => $"VIGILANT ({v:F2})",
                _ => "NEUTRAL"
            };

            sb.AppendLine("┌─────────────────────────────────────────────────┐");
            sb.AppendLine($"│ FEELING: {dominant,-38} │");
            sb.AppendLine("├─────────────────────────────────────────────────┤");
            sb.AppendLine("│ CONTROL KNOBS                                   │");
            sb.AppendLine($"│ PerceptionTrust  {Bar01(latest.PerceptionTrust / 1.5f)} {latest.PerceptionTrust,5:F2} │");
            sb.AppendLine($"│ CommitmentConf   {Bar01(latest.CommitmentConf / 2f)} {latest.CommitmentConf,5:F2} │");
            sb.AppendLine($"│ ActionSpeed      {Bar01(latest.ActionSpeed / 1.5f)} {latest.ActionSpeed,5:F2} │");
            sb.AppendLine($"│ LearningRate     {Bar01(latest.LearningRate / 2f)} {latest.LearningRate,5:F2} │");
            sb.AppendLine("├─────────────────────────────────────────────────┤");
            sb.AppendLine($"│ Gates: {(latest.ShouldHesitate ? "[HESITATE]" : "          ")} {(latest.MustActNow ? "[ACT NOW]" : "         ")}  │");
            sb.AppendLine($"│ GlobalStability  {Bar01(latest.GlobalStability)} {latest.GlobalStability,5:F2} │");
            sb.AppendLine("└─────────────────────────────────────────────────┘");

            return sb.ToString();
        }
    }

    /// <summary>
    /// Get live ASCII visualization of BeliefLock state.
    /// </summary>
    public string GetBeliefLockDisplay()
    {
        lock (_lock)
        {
            if (_history.Count == 0) return "No data";

            var latest = _history.Last();
            var sb = new StringBuilder();

            sb.AppendLine("┌─────────────────────────────────────────────────┐");
            sb.AppendLine($"│ BELIEF LOCK: {latest.CommittedMode,-10} → {latest.ExecutedMode,-10}      │");
            sb.AppendLine("├─────────────────────────────────────────────────┤");
            sb.AppendLine($"│ Status: {(latest.IsLocked ? "LOCKED" : "UNLOCKED"),-8} Frame: {latest.FramesSinceCommit,4}              │");
            sb.AppendLine($"│ Candidate: {(latest.HasCandidate ? $"YES ({latest.CandidateFrames} frames)" : "NO"),-20}         │");
            sb.AppendLine($"│ Forced Unlock: {(latest.ForcedUnlock ? "TRIGGERED" : "no"),-12}               │");
            sb.AppendLine($"│ Reason: {latest.CommitReason,-20}                 │");
            sb.AppendLine("├─────────────────────────────────────────────────┤");
            sb.AppendLine($"│ StrainTrend  {Bar(latest.StrainTrend, -0.5f, 0.5f)} {latest.StrainTrend,6:F3}  │");
            sb.AppendLine($"│ OutcomeTrend {Bar(latest.OutcomeTrendLock, -0.5f, 0.5f)} {latest.OutcomeTrendLock,6:F3}  │");
            sb.AppendLine("└─────────────────────────────────────────────────┘");

            return sb.ToString();
        }
    }

    /// <summary>
    /// Validate Z-score timing (should respond at different timescales).
    /// </summary>
    public string GetTimingValidation()
    {
        lock (_lock)
        {
            if (_z0Samples.Count < 30) return "Collecting timing data...";

            // Calculate variance over different time windows
            var z0List = _z0Samples.ToList();
            var z1List = _z1Samples.ToList();
            var z4List = _z4Samples.ToList();

            // Z₀ should have HIGH variance in short windows (reacts fast)
            // Z₄ should have LOW variance in short windows (reacts slow)

            float z0ShortVar = CalculateVariance(z0List.TakeLast(10).Select(x => x.Value));
            float z0LongVar = CalculateVariance(z0List.Select(x => x.Value));

            float z1ShortVar = CalculateVariance(z1List.TakeLast(10).Select(x => x.Value));
            float z1LongVar = CalculateVariance(z1List.Select(x => x.Value));

            float z4ShortVar = CalculateVariance(z4List.TakeLast(10).Select(x => x.Value));
            float z4LongVar = CalculateVariance(z4List.Select(x => x.Value));

            var sb = new StringBuilder();
            sb.AppendLine("┌─────────────────────────────────────────────────┐");
            sb.AppendLine("│ Z-SCORE TIMING VALIDATION                       │");
            sb.AppendLine("├─────────────────────────────────────────────────┤");
            sb.AppendLine($"│           Short(10f)  Long(90f)   Ratio        │");
            sb.AppendLine($"│ Z₀ var:   {z0ShortVar,8:F4}   {z0LongVar,8:F4}   {z0ShortVar / Math.Max(z0LongVar, 0.001f),5:F2}x  │");
            sb.AppendLine($"│ Z₁ var:   {z1ShortVar,8:F4}   {z1LongVar,8:F4}   {z1ShortVar / Math.Max(z1LongVar, 0.001f),5:F2}x  │");
            sb.AppendLine($"│ Z₄ var:   {z4ShortVar,8:F4}   {z4LongVar,8:F4}   {z4ShortVar / Math.Max(z4LongVar, 0.001f),5:F2}x  │");
            sb.AppendLine("├─────────────────────────────────────────────────┤");

            // Check timing rules
            bool z0Fast = z0ShortVar > z4ShortVar;
            bool z4Slow = z4ShortVar < z1ShortVar;

            sb.AppendLine($"│ Z₀ faster than Z₄: {(z0Fast ? "OK" : "WARN")}                          │");
            sb.AppendLine($"│ Z₄ slower than Z₁: {(z4Slow ? "OK" : "WARN")}                          │");
            sb.AppendLine("└─────────────────────────────────────────────────┘");

            return sb.ToString();
        }
    }

    /// <summary>
    /// Get complete dashboard.
    /// </summary>
    public string GetDashboard()
    {
        var sb = new StringBuilder();
        sb.AppendLine(GetZScoreDisplay());
        sb.AppendLine(GetFeelingDisplay());
        sb.AppendLine(GetBeliefLockDisplay());
        sb.AppendLine(GetTimingValidation());
        return sb.ToString();
    }

    /// <summary>
    /// Export history to CSV for analysis.
    /// </summary>
    public string ExportCsv()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            sb.AppendLine("timestamp,frame,z0_vision,z0_audio,z1_agree,z2_stable,z3_control,z4_coher,strain," +
                          "pred_err,threat,control_conf,sensory_align,outcome_trend,global_stab," +
                          "perc_trust,commit_conf,action_speed,learn_rate,hesitate,act_now," +
                          "anxiety,frustration,focus,vigilance," +
                          "committed_mode,executed_mode,locked,forced_unlock,commit_reason");

            foreach (var e in _history)
            {
                sb.AppendLine($"{e.Timestamp:O},{e.FrameNumber}," +
                              $"{e.Z0_VisionMotion:F4},{e.Z0_AudioLevel:F4},{e.Z1_Agreement:F4},{e.Z2_Stability:F4},{e.Z3_Control:F4},{e.Z4_Coherence:F4},{e.SystemStrain:F4}," +
                              $"{e.PredictionError:F4},{e.ThreatPressure:F4},{e.ControlConfidence:F4},{e.SensoryAlignment:F4},{e.OutcomeTrend:F4},{e.GlobalStability:F4}," +
                              $"{e.PerceptionTrust:F4},{e.CommitmentConf:F4},{e.ActionSpeed:F4},{e.LearningRate:F4},{e.ShouldHesitate},{e.MustActNow}," +
                              $"{e.Anxiety:F4},{e.Frustration:F4},{e.Focus:F4},{e.Vigilance:F4}," +
                              $"{e.CommittedMode},{e.ExecutedMode},{e.IsLocked},{e.ForcedUnlock},{e.CommitReason}");
            }

            return sb.ToString();
        }
    }

    private static string Bar(float value, float min, float max)
    {
        const int width = 20;
        float normalized = (value - min) / (max - min);
        normalized = Math.Clamp(normalized, 0, 1);

        int filled = (int)(normalized * width);
        int center = (int)((-min / (max - min)) * width);

        var chars = new char[width];
        for (int i = 0; i < width; i++)
        {
            if (i == center) chars[i] = '│';
            else if (value >= 0 && i > center && i <= filled) chars[i] = '█';
            else if (value < 0 && i < center && i >= filled) chars[i] = '█';
            else chars[i] = '░';
        }

        return new string(chars);
    }

    private static string Bar01(float value)
    {
        const int width = 20;
        float normalized = Math.Clamp(value, 0, 1);
        int filled = (int)(normalized * width);

        var chars = new char[width];
        for (int i = 0; i < width; i++)
        {
            chars[i] = i < filled ? '█' : '░';
        }

        return new string(chars);
    }

    private static float CalculateVariance(IEnumerable<float> values)
    {
        var list = values.ToList();
        if (list.Count < 2) return 0;

        float mean = list.Average();
        return list.Average(v => (v - mean) * (v - mean));
    }

    private sealed record TraceEntry
    {
        public DateTime Timestamp { get; init; }
        public int FrameNumber { get; init; }

        // Z-scores
        public float Z0_VisionMotion { get; init; }
        public float Z0_VisionThreats { get; init; }
        public float Z0_AudioLevel { get; init; }
        public float Z0_AudioThreats { get; init; }
        public float Z1_Agreement { get; init; }
        public float Z2_Stability { get; init; }
        public float Z3_Control { get; init; }
        public float Z4_Coherence { get; init; }
        public float SystemStrain { get; init; }

        // Feeling raw
        public float PredictionError { get; init; }
        public float ThreatPressure { get; init; }
        public float ControlConfidence { get; init; }
        public float SensoryAlignment { get; init; }
        public float OutcomeTrend { get; init; }
        public float GlobalStability { get; init; }

        // Control knobs
        public float PerceptionTrust { get; init; }
        public float CommitmentConf { get; init; }
        public float ActionSpeed { get; init; }
        public float LearningRate { get; init; }
        public bool ShouldHesitate { get; init; }
        public bool MustActNow { get; init; }

        // Emotions
        public float Anxiety { get; init; }
        public float Frustration { get; init; }
        public float Focus { get; init; }
        public float Vigilance { get; init; }

        // BeliefLock
        public StrategicMode CommittedMode { get; init; }
        public StrategicMode ExecutedMode { get; init; }
        public bool IsLocked { get; init; }
        public int FramesSinceCommit { get; init; }
        public bool HasCandidate { get; init; }
        public int CandidateFrames { get; init; }
        public bool ForcedUnlock { get; init; }
        public float StrainTrend { get; init; }
        public float OutcomeTrendLock { get; init; }
        public string CommitReason { get; init; } = "";
    }
}
