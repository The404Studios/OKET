namespace OKET.Core.Detection;

/// <summary>
/// Result from running object detection on a frame.
/// </summary>
public sealed class DetectionResult
{
    /// <summary>Frame ID these detections came from.</summary>
    public long FrameId { get; init; }

    /// <summary>Time taken for detection (ms).</summary>
    public float InferenceTimeMs { get; init; }

    /// <summary>All detected objects.</summary>
    public IReadOnlyList<Detection> Detections { get; init; } = [];

    /// <summary>Get detections of a specific class.</summary>
    public IEnumerable<Detection> OfClass(DetectionClass cls) =>
        Detections.Where(d => d.Class == cls);

    /// <summary>Get all threat detections, sorted by priority.</summary>
    public IEnumerable<Detection> Threats =>
        Detections.Where(d => d.IsThreat).OrderByDescending(d => d.Priority);

    /// <summary>Get the highest priority threat.</summary>
    public Detection? PrimaryThreat =>
        Threats.FirstOrDefault();

    /// <summary>Total number of threats detected.</summary>
    public int ThreatCount => Detections.Count(d => d.IsThreat);

    /// <summary>Get all item detections (interactable objects).</summary>
    public IEnumerable<Detection> Items =>
        Detections.Where(d => d.IsInteractable);
}
