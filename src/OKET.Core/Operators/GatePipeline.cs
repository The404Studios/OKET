namespace OKET.Core.Operators;

/// <summary>
/// Complete Pipeline with Gates at Every Stage.
///
/// ARCHITECTURE:
///
///   PREPROCESSING ──────────────────────────────────────────────────
///   │ gate │ Grayscale (geometry, edges, motion)
///   │ gate │ Color (UI, enemies, items)
///   │ gate │ ROI routing (minimap, center screen, HUD)
///   └──────┘
///       ↓ gate
///   FEATURE EXTRACTION ─────────────────────────────────────────────
///   │ gate │ Edges / contours
///   │ gate │ Motion vectors
///   │ gate │ Color masks
///   │ gate │ Temporal tracking
///   └──────┘
///       ↓ gate
///   STATE ESTIMATION ───────────────────────────────────────────────
///   │ gate │ Enemy positions
///   │ gate │ Threat direction
///   │ gate │ Navigable space
///   │ gate │ Confidence scores
///   └──────┘
///       ↓ gate
///   DECISION LAYER ─────────────────────────────────────────────────
///   │ gate │ Aim / move / shoot
///   │ gate │ Delay / pause / disengage
///   │ gate │ Exploration vs exploitation
///   └──────┘
///       ↓ gate
///   ACTUATION ──────────────────────────────────────────────────────
///   │ gate │ Mouse
///   │ gate │ Keyboard
///   └──────┘
///
/// Each stage feeds its output gate result to the next stage's input gate.
/// Backpropagation flows in reverse: actuation → decision → state → feature → preprocessing.
/// </summary>
public sealed class GatePipeline
{
    // All stages with their gates
    public PipelineGate Preprocessing { get; }
    public PipelineGate FeatureExtraction { get; }
    public PipelineGate StateEstimation { get; }
    public PipelineGate DecisionLayer { get; }
    public PipelineGate Actuation { get; }

    // Inter-stage connections
    private readonly StageConnection _prepToFeature;
    private readonly StageConnection _featureToState;
    private readonly StageConnection _stateToDecision;
    private readonly StageConnection _decisionToActuation;

    // Global pipeline state
    private float _pipelineGain = 1f;
    private float _globalErrorGradient;
    private bool _isStable = true;

    /// <summary>Overall pipeline gain (output/input).</summary>
    public float PipelineGain => _pipelineGain;

    /// <summary>Is pipeline in stable state?</summary>
    public bool IsStable => _isStable;

    /// <summary>Global error gradient for learning.</summary>
    public float GlobalErrorGradient => _globalErrorGradient;

    public GatePipeline()
    {
        // Create stages with their channels
        Preprocessing = new PipelineGate("Preprocessing",
            "Grayscale", "Color", "ROI_Minimap", "ROI_Center", "ROI_HUD");

        FeatureExtraction = new PipelineGate("FeatureExtraction",
            "Edges", "Contours", "Motion", "ColorMasks", "TemporalTracking");

        StateEstimation = new PipelineGate("StateEstimation",
            "EnemyPositions", "ThreatDirection", "NavigableSpace", "Confidence");

        DecisionLayer = new PipelineGate("DecisionLayer",
            "Aim", "Move", "Shoot", "Delay", "Disengage", "Explore", "Exploit");

        Actuation = new PipelineGate("Actuation",
            "Mouse_X", "Mouse_Y", "Mouse_Click", "Key_W", "Key_A", "Key_S", "Key_D",
            "Key_Space", "Key_Shift", "Key_R", "Key_E");

        // Create inter-stage connections
        _prepToFeature = new StageConnection(Preprocessing, FeatureExtraction);
        _featureToState = new StageConnection(FeatureExtraction, StateEstimation);
        _stateToDecision = new StageConnection(StateEstimation, DecisionLayer);
        _decisionToActuation = new StageConnection(DecisionLayer, Actuation);
    }

    /// <summary>
    /// Process one full pipeline cycle.
    /// Returns the overall pipeline result.
    /// </summary>
    public PipelineCycleResult ProcessCycle(
        PreprocessingInput prepInput,
        float urgency = 0.5f)
    {
        var result = new PipelineCycleResult();

        // === STAGE 1: PREPROCESSING ===
        var prepOutputs = ProcessPreprocessing(prepInput, urgency);
        result.PreprocessingOutput = prepOutputs;

        // === STAGE 2: FEATURE EXTRACTION ===
        var featureInputs = _prepToFeature.TransferSignals(prepOutputs);
        var featureOutputs = ProcessFeatureExtraction(featureInputs, urgency);
        result.FeatureOutput = featureOutputs;

        // === STAGE 3: STATE ESTIMATION ===
        var stateInputs = _featureToState.TransferSignals(featureOutputs);
        var stateOutputs = ProcessStateEstimation(stateInputs, urgency);
        result.StateOutput = stateOutputs;

        // === STAGE 4: DECISION LAYER ===
        var decisionInputs = _stateToDecision.TransferSignals(stateOutputs);
        var decisionOutputs = ProcessDecisionLayer(decisionInputs, urgency);
        result.DecisionOutput = decisionOutputs;

        // === STAGE 5: ACTUATION ===
        var actuationInputs = _decisionToActuation.TransferSignals(decisionOutputs);
        var actuationOutputs = ProcessActuation(actuationInputs, urgency);
        result.ActuationOutput = actuationOutputs;

        // Calculate pipeline gain
        float totalInput = prepInput.TotalEnergy;
        float totalOutput = actuationOutputs.Values.Sum(s => s.Signal);
        _pipelineGain = totalInput > 0.01f ? totalOutput / totalInput : 1f;

        result.PipelineGain = _pipelineGain;
        result.Success = _pipelineGain >= 0.8f;

        return result;
    }

    /// <summary>
    /// Apply backpropagation through the entire pipeline.
    /// Call after we know the outcome of the cycle.
    /// </summary>
    public void Backpropagate(float outcomeScore, float errorSignal)
    {
        _globalErrorGradient = _globalErrorGradient * 0.9f + errorSignal * 0.1f;

        // Backprop from actuation backward
        Actuation.RecordOutcome(outcomeScore, errorSignal);

        float actuationGradient = Actuation.GetUpstreamGradient();
        DecisionLayer.RecordOutcome(outcomeScore, actuationGradient);

        float decisionGradient = DecisionLayer.GetUpstreamGradient();
        StateEstimation.RecordOutcome(outcomeScore, decisionGradient);

        float stateGradient = StateEstimation.GetUpstreamGradient();
        FeatureExtraction.RecordOutcome(outcomeScore, stateGradient);

        float featureGradient = FeatureExtraction.GetUpstreamGradient();
        Preprocessing.RecordOutcome(outcomeScore, featureGradient);

        // Update stability
        _isStable = Math.Abs(_pipelineGain - 1f) < 0.2f &&
                   Math.Abs(_globalErrorGradient) < 0.3f;
    }

    private Dictionary<string, GatedSignal> ProcessPreprocessing(PreprocessingInput input, float urgency)
    {
        var outputs = new Dictionary<string, GatedSignal>();

        // Gate grayscale pipeline
        var grayIn = Preprocessing.GateInput("Grayscale", input.GrayscaleStrength, urgency);
        if (grayIn.Permitted)
        {
            float processed = grayIn.Signal * 0.9f; // Processing loss
            outputs["Grayscale"] = Preprocessing.GateOutput("Grayscale", processed, input.Quality);
        }

        // Gate color pipeline
        var colorIn = Preprocessing.GateInput("Color", input.ColorStrength, urgency);
        if (colorIn.Permitted)
        {
            float processed = colorIn.Signal * 0.85f;
            outputs["Color"] = Preprocessing.GateOutput("Color", processed, input.Quality);
        }

        // Gate ROI channels
        foreach (var (roiName, roiValue) in input.ROIValues)
        {
            var roiIn = Preprocessing.GateInput($"ROI_{roiName}", roiValue, urgency);
            if (roiIn.Permitted)
            {
                outputs[$"ROI_{roiName}"] = Preprocessing.GateOutput($"ROI_{roiName}", roiIn.Signal, input.Quality);
            }
        }

        return outputs;
    }

    private Dictionary<string, GatedSignal> ProcessFeatureExtraction(
        Dictionary<string, GatedSignal> inputs, float urgency)
    {
        var outputs = new Dictionary<string, GatedSignal>();
        float inputStrength = inputs.Values.Where(s => s.Permitted).Sum(s => s.Signal) / Math.Max(1, inputs.Count);

        // Extract edges from grayscale
        if (inputs.TryGetValue("Grayscale", out var gray) && gray.Permitted)
        {
            var edgeIn = FeatureExtraction.GateInput("Edges", gray.Signal * 0.8f, urgency);
            if (edgeIn.Permitted)
                outputs["Edges"] = FeatureExtraction.GateOutput("Edges", edgeIn.Signal, gray.Modulation);

            var contourIn = FeatureExtraction.GateInput("Contours", gray.Signal * 0.7f, urgency);
            if (contourIn.Permitted)
                outputs["Contours"] = FeatureExtraction.GateOutput("Contours", contourIn.Signal, gray.Modulation);
        }

        // Extract color masks from color
        if (inputs.TryGetValue("Color", out var color) && color.Permitted)
        {
            var maskIn = FeatureExtraction.GateInput("ColorMasks", color.Signal * 0.9f, urgency);
            if (maskIn.Permitted)
                outputs["ColorMasks"] = FeatureExtraction.GateOutput("ColorMasks", maskIn.Signal, color.Modulation);
        }

        // Motion from temporal difference
        var motionIn = FeatureExtraction.GateInput("Motion", inputStrength * 0.6f, urgency);
        if (motionIn.Permitted)
            outputs["Motion"] = FeatureExtraction.GateOutput("Motion", motionIn.Signal, 0.5f);

        // Temporal tracking
        var tempIn = FeatureExtraction.GateInput("TemporalTracking", inputStrength * 0.7f, urgency);
        if (tempIn.Permitted)
            outputs["TemporalTracking"] = FeatureExtraction.GateOutput("TemporalTracking", tempIn.Signal, 0.5f);

        return outputs;
    }

    private Dictionary<string, GatedSignal> ProcessStateEstimation(
        Dictionary<string, GatedSignal> inputs, float urgency)
    {
        var outputs = new Dictionary<string, GatedSignal>();
        float featureStrength = inputs.Values.Where(s => s.Permitted).Sum(s => s.Signal) / Math.Max(1, inputs.Count);

        // Enemy positions from color masks and motion
        float enemySignal = 0;
        if (inputs.TryGetValue("ColorMasks", out var masks) && masks.Permitted)
            enemySignal += masks.Signal * 0.5f;
        if (inputs.TryGetValue("Motion", out var motion) && motion.Permitted)
            enemySignal += motion.Signal * 0.3f;

        var enemyIn = StateEstimation.GateInput("EnemyPositions", enemySignal, urgency);
        if (enemyIn.Permitted)
            outputs["EnemyPositions"] = StateEstimation.GateOutput("EnemyPositions", enemyIn.Signal, 0.6f);

        // Threat direction from edges and motion
        float threatSignal = 0;
        if (inputs.TryGetValue("Edges", out var edges) && edges.Permitted)
            threatSignal += edges.Signal * 0.3f;
        if (motion.Permitted)
            threatSignal += motion.Signal * 0.5f;

        var threatIn = StateEstimation.GateInput("ThreatDirection", threatSignal, urgency);
        if (threatIn.Permitted)
            outputs["ThreatDirection"] = StateEstimation.GateOutput("ThreatDirection", threatIn.Signal, 0.5f);

        // Navigable space from edges and contours
        float navSignal = 0;
        if (edges.Permitted)
            navSignal += edges.Signal * 0.4f;
        if (inputs.TryGetValue("Contours", out var contours) && contours.Permitted)
            navSignal += contours.Signal * 0.4f;

        var navIn = StateEstimation.GateInput("NavigableSpace", navSignal, urgency);
        if (navIn.Permitted)
            outputs["NavigableSpace"] = StateEstimation.GateOutput("NavigableSpace", navIn.Signal, 0.5f);

        // Confidence from temporal tracking
        var confIn = StateEstimation.GateInput("Confidence", featureStrength, urgency);
        if (confIn.Permitted)
            outputs["Confidence"] = StateEstimation.GateOutput("Confidence", confIn.Signal, 0.7f);

        return outputs;
    }

    private Dictionary<string, GatedSignal> ProcessDecisionLayer(
        Dictionary<string, GatedSignal> inputs, float urgency)
    {
        var outputs = new Dictionary<string, GatedSignal>();

        // Get state signals
        float enemyStrength = inputs.TryGetValue("EnemyPositions", out var enemy) && enemy.Permitted
            ? enemy.Signal : 0;
        float threatStrength = inputs.TryGetValue("ThreatDirection", out var threat) && threat.Permitted
            ? threat.Signal : 0;
        float navStrength = inputs.TryGetValue("NavigableSpace", out var nav) && nav.Permitted
            ? nav.Signal : 0;
        float confidence = inputs.TryGetValue("Confidence", out var conf) && conf.Permitted
            ? conf.Signal : 0.5f;

        // Decision: Aim (based on enemy and confidence)
        var aimIn = DecisionLayer.GateInput("Aim", enemyStrength * confidence, urgency);
        if (aimIn.Permitted)
            outputs["Aim"] = DecisionLayer.GateOutput("Aim", aimIn.Signal, confidence);

        // Decision: Move (based on threat and navigable space)
        var moveIn = DecisionLayer.GateInput("Move", navStrength * 0.7f + threatStrength * 0.3f, urgency);
        if (moveIn.Permitted)
            outputs["Move"] = DecisionLayer.GateOutput("Move", moveIn.Signal, confidence);

        // Decision: Shoot (based on enemy, confidence, and low threat)
        float shootSignal = enemyStrength * confidence * (1f - threatStrength * 0.3f);
        var shootIn = DecisionLayer.GateInput("Shoot", shootSignal, urgency);
        if (shootIn.Permitted)
            outputs["Shoot"] = DecisionLayer.GateOutput("Shoot", shootIn.Signal, confidence);

        // Decision: Delay (when uncertain)
        if (confidence < 0.4f)
        {
            var delayIn = DecisionLayer.GateInput("Delay", 1f - confidence, urgency * 0.5f);
            if (delayIn.Permitted)
                outputs["Delay"] = DecisionLayer.GateOutput("Delay", delayIn.Signal, 0.5f);
        }

        // Decision: Disengage (when threat high, confidence low)
        if (threatStrength > 0.6f && confidence < 0.5f)
        {
            var disIn = DecisionLayer.GateInput("Disengage", threatStrength, urgency);
            if (disIn.Permitted)
                outputs["Disengage"] = DecisionLayer.GateOutput("Disengage", disIn.Signal, 0.6f);
        }

        // Exploration vs Exploitation
        float exploreSignal = 1f - enemyStrength - threatStrength;
        var exploreIn = DecisionLayer.GateInput("Explore", exploreSignal, urgency * 0.3f);
        if (exploreIn.Permitted)
            outputs["Explore"] = DecisionLayer.GateOutput("Explore", exploreIn.Signal, 0.4f);

        var exploitIn = DecisionLayer.GateInput("Exploit", enemyStrength * 0.5f + threatStrength * 0.5f, urgency);
        if (exploitIn.Permitted)
            outputs["Exploit"] = DecisionLayer.GateOutput("Exploit", exploitIn.Signal, confidence);

        return outputs;
    }

    private Dictionary<string, GatedSignal> ProcessActuation(
        Dictionary<string, GatedSignal> inputs, float urgency)
    {
        var outputs = new Dictionary<string, GatedSignal>();

        // Map decisions to actuation
        if (inputs.TryGetValue("Aim", out var aim) && aim.Permitted)
        {
            // Convert aim signal to mouse movement
            var mouseXIn = Actuation.GateInput("Mouse_X", aim.Signal * 0.8f, urgency);
            if (mouseXIn.Permitted)
                outputs["Mouse_X"] = Actuation.GateOutput("Mouse_X", mouseXIn.Signal, aim.Modulation);

            var mouseYIn = Actuation.GateInput("Mouse_Y", aim.Signal * 0.6f, urgency);
            if (mouseYIn.Permitted)
                outputs["Mouse_Y"] = Actuation.GateOutput("Mouse_Y", mouseYIn.Signal, aim.Modulation);
        }

        if (inputs.TryGetValue("Shoot", out var shoot) && shoot.Permitted)
        {
            var clickIn = Actuation.GateInput("Mouse_Click", shoot.Signal, urgency);
            if (clickIn.Permitted)
                outputs["Mouse_Click"] = Actuation.GateOutput("Mouse_Click", clickIn.Signal, shoot.Modulation);
        }

        if (inputs.TryGetValue("Move", out var move) && move.Permitted)
        {
            // Movement keys based on threat direction (simplified)
            var wIn = Actuation.GateInput("Key_W", move.Signal * 0.7f, urgency);
            if (wIn.Permitted)
                outputs["Key_W"] = Actuation.GateOutput("Key_W", wIn.Signal, move.Modulation);

            // Add strafing if disengage
            if (inputs.TryGetValue("Disengage", out var dis) && dis.Permitted)
            {
                var aIn = Actuation.GateInput("Key_A", dis.Signal * 0.5f, urgency);
                if (aIn.Permitted)
                    outputs["Key_A"] = Actuation.GateOutput("Key_A", aIn.Signal, dis.Modulation);
            }
        }

        if (inputs.TryGetValue("Delay", out var delay) && delay.Permitted)
        {
            // Suppress all outputs when delaying
            outputs.Clear();
        }

        return outputs;
    }

    /// <summary>
    /// Get comprehensive diagnostics.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"""
            === GATE PIPELINE ===
            Pipeline Gain: {_pipelineGain:F2}, Stable: {_isStable}
            Global Error: {_globalErrorGradient:+0.00;-0.00}

            {Preprocessing.GetDiagnostics()}
            {FeatureExtraction.GetDiagnostics()}
            {StateEstimation.GetDiagnostics()}
            {DecisionLayer.GetDiagnostics()}
            {Actuation.GetDiagnostics()}
            =====================
            """;
    }
}

/// <summary>
/// Connection between two pipeline stages.
/// Handles signal transfer and modulation between stages.
/// </summary>
public sealed class StageConnection
{
    private readonly PipelineGate _source;
    private readonly PipelineGate _target;
    private float _transferEfficiency = 1f;

    public StageConnection(PipelineGate source, PipelineGate target)
    {
        _source = source;
        _target = target;
    }

    /// <summary>
    /// Transfer signals from source output to target input.
    /// </summary>
    public Dictionary<string, GatedSignal> TransferSignals(Dictionary<string, GatedSignal> sourceOutputs)
    {
        // Apply downstream modulation from source
        float downstreamMod = _source.GetDownstreamModulation();

        var transferred = new Dictionary<string, GatedSignal>();
        foreach (var (name, signal) in sourceOutputs)
        {
            if (signal.Permitted)
            {
                transferred[name] = signal with
                {
                    Signal = signal.Signal * _transferEfficiency * downstreamMod,
                    Modulation = signal.Modulation * downstreamMod
                };
            }
        }

        // Adapt transfer efficiency based on target feedback
        float upstreamGrad = _target.GetUpstreamGradient();
        _transferEfficiency = Math.Clamp(_transferEfficiency - upstreamGrad * 0.01f, 0.5f, 1.2f);

        return transferred;
    }
}

/// <summary>
/// Input to the preprocessing stage.
/// </summary>
public readonly struct PreprocessingInput
{
    public float GrayscaleStrength { get; init; }
    public float ColorStrength { get; init; }
    public float Quality { get; init; }
    public Dictionary<string, float> ROIValues { get; init; }

    public float TotalEnergy => GrayscaleStrength + ColorStrength +
        (ROIValues?.Values.Sum() ?? 0);
}

/// <summary>
/// Result of a full pipeline cycle.
/// </summary>
public sealed class PipelineCycleResult
{
    public Dictionary<string, GatedSignal> PreprocessingOutput { get; set; } = new();
    public Dictionary<string, GatedSignal> FeatureOutput { get; set; } = new();
    public Dictionary<string, GatedSignal> StateOutput { get; set; } = new();
    public Dictionary<string, GatedSignal> DecisionOutput { get; set; } = new();
    public Dictionary<string, GatedSignal> ActuationOutput { get; set; } = new();
    public float PipelineGain { get; set; }
    public bool Success { get; set; }
}
