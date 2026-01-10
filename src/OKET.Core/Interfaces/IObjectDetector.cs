using OKET.Core.Types;
using OKET.Core.Detection;

namespace OKET.Core.Interfaces;

/// <summary>
/// Detects objects (zombies, items, etc.) in frames.
/// </summary>
public interface IObjectDetector : IDisposable
{
    /// <summary>Whether the model is loaded and ready.</summary>
    bool IsReady { get; }

    /// <summary>Load the detection model.</summary>
    Task LoadAsync(string modelPath, CancellationToken ct = default);

    /// <summary>Run detection on a frame.</summary>
    Task<DetectionResult> DetectAsync(Frame frame, CancellationToken ct = default);

    /// <summary>Minimum confidence threshold [0, 1].</summary>
    float ConfidenceThreshold { get; set; }

    /// <summary>Classes this detector can identify.</summary>
    IReadOnlyList<DetectionClass> SupportedClasses { get; }
}
