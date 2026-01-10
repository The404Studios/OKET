using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OKET.Core.Types;
using OKET.Core.Interfaces;
using CoreDetection = OKET.Core.Detection;

namespace OKET.Vision.Detection;

/// <summary>
/// Object detector using ONNX Runtime for inference.
/// Supports YOLO-style models for detecting game entities.
/// </summary>
public sealed class OnnxObjectDetector : IObjectDetector
{
    private InferenceSession? _session;
    private string[]? _inputNames;
    private string[]? _outputNames;
    private int _inputWidth = 640;
    private int _inputHeight = 640;

    public bool IsReady => _session != null;
    public float ConfidenceThreshold { get; set; } = 0.5f;

    public IReadOnlyList<CoreDetection.DetectionClass> SupportedClasses { get; } = new[]
    {
        CoreDetection.DetectionClass.Zombie,
        CoreDetection.DetectionClass.ZombieHead,
        CoreDetection.DetectionClass.FastZombie,
        CoreDetection.DetectionClass.PoisonZombie,
        CoreDetection.DetectionClass.Headcrab,
        CoreDetection.DetectionClass.Barricade,
        CoreDetection.DetectionClass.BarricadeBoard,
        CoreDetection.DetectionClass.AmmoCrate,
        CoreDetection.DetectionClass.WeaponCrate,
        CoreDetection.DetectionClass.HealthKit,
        CoreDetection.DetectionClass.Survivor,
        CoreDetection.DetectionClass.Prop
    };

    // Maps model class indices to our detection classes
    private readonly Dictionary<int, CoreDetection.DetectionClass> _classMapping = new()
    {
        { 0, CoreDetection.DetectionClass.Zombie },
        { 1, CoreDetection.DetectionClass.ZombieHead },
        { 2, CoreDetection.DetectionClass.FastZombie },
        { 3, CoreDetection.DetectionClass.PoisonZombie },
        { 4, CoreDetection.DetectionClass.Headcrab },
        { 5, CoreDetection.DetectionClass.Barricade },
        { 6, CoreDetection.DetectionClass.BarricadeBoard },
        { 7, CoreDetection.DetectionClass.AmmoCrate },
        { 8, CoreDetection.DetectionClass.WeaponCrate },
        { 9, CoreDetection.DetectionClass.HealthKit },
        { 10, CoreDetection.DetectionClass.Survivor },
        { 11, CoreDetection.DetectionClass.Prop }
    };

    public async Task LoadAsync(string modelPath, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_PARALLEL
            };

            // Try to use GPU if available
            try
            {
                options.AppendExecutionProvider_CUDA();
            }
            catch
            {
                // CUDA not available, fall back to CPU
            }

            _session = new InferenceSession(modelPath, options);

            // Get input/output info
            _inputNames = _session.InputMetadata.Keys.ToArray();
            _outputNames = _session.OutputMetadata.Keys.ToArray();

            // Get expected input dimensions from model
            var inputMeta = _session.InputMetadata.First().Value;
            if (inputMeta.Dimensions.Length >= 4)
            {
                _inputHeight = inputMeta.Dimensions[2];
                _inputWidth = inputMeta.Dimensions[3];
            }
        }, ct);
    }

    public async Task<CoreDetection.DetectionResult> DetectAsync(Frame frame, CancellationToken ct = default)
    {
        if (_session == null || _inputNames == null || _outputNames == null)
        {
            return new CoreDetection.DetectionResult { FrameId = frame.Id, Detections = [] };
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        return await Task.Run(() =>
        {
            // Preprocess: resize and normalize
            var inputTensor = PreprocessFrame(frame);

            // Run inference
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputNames[0], inputTensor)
            };

            using var results = _session.Run(inputs);

            // Parse output (YOLO format: [batch, num_detections, 85] or similar)
            var outputTensor = results.First().AsTensor<float>();
            var detections = ParseYoloOutput(outputTensor, frame.Width, frame.Height);

            sw.Stop();

            return new CoreDetection.DetectionResult
            {
                FrameId = frame.Id,
                InferenceTimeMs = sw.ElapsedMilliseconds,
                Detections = detections
            };
        }, ct);
    }

    private DenseTensor<float> PreprocessFrame(Frame frame)
    {
        // Create tensor: [1, 3, height, width] in RGB normalized to [0,1]
        var tensor = new DenseTensor<float>(new[] { 1, 3, _inputHeight, _inputWidth });

        float scaleX = frame.Width / (float)_inputWidth;
        float scaleY = frame.Height / (float)_inputHeight;

        for (int y = 0; y < _inputHeight; y++)
        {
            for (int x = 0; x < _inputWidth; x++)
            {
                int srcX = (int)(x * scaleX);
                int srcY = (int)(y * scaleY);

                var (b, g, r, _) = frame.GetPixel(srcX, srcY);

                // Normalize to [0, 1] and convert BGR to RGB
                tensor[0, 0, y, x] = r / 255f;
                tensor[0, 1, y, x] = g / 255f;
                tensor[0, 2, y, x] = b / 255f;
            }
        }

        return tensor;
    }

    private List<CoreDetection.Detection> ParseYoloOutput(Tensor<float> output, int frameWidth, int frameHeight)
    {
        var detections = new List<CoreDetection.Detection>();
        var dims = output.Dimensions.ToArray();

        // YOLO output format: [batch, num_predictions, 5 + num_classes]
        // Each prediction: [x_center, y_center, width, height, obj_conf, class_probs...]
        if (dims.Length < 2) return detections;

        int numPredictions = dims.Length == 3 ? dims[1] : dims[0];
        int predictionSize = dims.Length == 3 ? dims[2] : dims[1];
        int numClasses = predictionSize - 5;

        float scaleX = frameWidth / (float)_inputWidth;
        float scaleY = frameHeight / (float)_inputHeight;

        for (int i = 0; i < numPredictions; i++)
        {
            // Get object confidence
            float objConf = dims.Length == 3 ? output[0, i, 4] : output[i, 4];
            if (objConf < ConfidenceThreshold) continue;

            // Find best class
            int bestClass = 0;
            float bestClassProb = 0;
            for (int c = 0; c < numClasses && c < _classMapping.Count; c++)
            {
                float classProb = dims.Length == 3 ? output[0, i, 5 + c] : output[i, 5 + c];
                if (classProb > bestClassProb)
                {
                    bestClassProb = classProb;
                    bestClass = c;
                }
            }

            float confidence = objConf * bestClassProb;
            if (confidence < ConfidenceThreshold) continue;

            // Get bounding box (center format -> corner format)
            float cx = dims.Length == 3 ? output[0, i, 0] : output[i, 0];
            float cy = dims.Length == 3 ? output[0, i, 1] : output[i, 1];
            float w = dims.Length == 3 ? output[0, i, 2] : output[i, 2];
            float h = dims.Length == 3 ? output[0, i, 3] : output[i, 3];

            // Scale to frame coordinates
            float x = (cx - w / 2) * scaleX;
            float y = (cy - h / 2) * scaleY;
            w *= scaleX;
            h *= scaleY;

            // Map to detection class
            if (!_classMapping.TryGetValue(bestClass, out var detectionClass))
                detectionClass = CoreDetection.DetectionClass.Unknown;

            detections.Add(new CoreDetection.Detection
            {
                Class = detectionClass,
                Confidence = confidence,
                Box = new BoundingBox(x, y, w, h),
                FrameId = 0, // Will be set by caller
                Priority = CalculatePriority(detectionClass, confidence, cx, cy, frameWidth, frameHeight)
            });
        }

        // Apply Non-Maximum Suppression
        return ApplyNms(detections, 0.45f);
    }

    private float CalculatePriority(CoreDetection.DetectionClass cls, float confidence, float cx, float cy,
        int frameWidth, int frameHeight)
    {
        // Priority based on:
        // 1. Class threat level
        // 2. Confidence
        // 3. Distance from center (closer = higher priority)

        float classPriority = cls switch
        {
            CoreDetection.DetectionClass.FastZombie => 1.5f,
            CoreDetection.DetectionClass.Zombie => 1.0f,
            CoreDetection.DetectionClass.PoisonZombie => 1.2f,
            CoreDetection.DetectionClass.Headcrab => 0.8f,
            CoreDetection.DetectionClass.ZombieHead => 1.3f,
            _ => 0.5f
        };

        // Distance from center (normalized 0-1, 0 = center)
        float dx = (cx - frameWidth / 2f) / (frameWidth / 2f);
        float dy = (cy - frameHeight / 2f) / (frameHeight / 2f);
        float distFromCenter = MathF.Sqrt(dx * dx + dy * dy);

        // Closer to center = higher priority
        float positionBonus = 1f - Math.Clamp(distFromCenter, 0f, 1f);

        return classPriority * confidence * (0.5f + 0.5f * positionBonus);
    }

    private List<CoreDetection.Detection> ApplyNms(List<CoreDetection.Detection> detections, float iouThreshold)
    {
        if (detections.Count == 0) return detections;

        // Sort by confidence
        var sorted = detections.OrderByDescending(d => d.Confidence).ToList();
        var keep = new List<CoreDetection.Detection>();
        var suppressed = new HashSet<int>();

        for (int i = 0; i < sorted.Count; i++)
        {
            if (suppressed.Contains(i)) continue;

            keep.Add(sorted[i]);

            for (int j = i + 1; j < sorted.Count; j++)
            {
                if (suppressed.Contains(j)) continue;

                float iou = CalculateIoU(sorted[i].Box, sorted[j].Box);
                if (iou > iouThreshold)
                {
                    suppressed.Add(j);
                }
            }
        }

        return keep;
    }

    private float CalculateIoU(BoundingBox a, BoundingBox b)
    {
        float x1 = Math.Max(a.Left, b.Left);
        float y1 = Math.Max(a.Top, b.Top);
        float x2 = Math.Min(a.Right, b.Right);
        float y2 = Math.Min(a.Bottom, b.Bottom);

        float intersection = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        float union = a.Area + b.Area - intersection;

        return union > 0 ? intersection / union : 0;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
