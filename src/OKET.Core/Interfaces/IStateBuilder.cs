using OKET.Core.Types;
using OKET.Core.State;
using OKET.Core.Detection;

namespace OKET.Core.Interfaces;

/// <summary>
/// Builds game state from raw perceptions.
/// </summary>
public interface IStateBuilder
{
    /// <summary>
    /// Build a complete game state from frame data and detections.
    /// </summary>
    GameState Build(
        Frame frame,
        HudState hud,
        DetectionResult detections,
        GameState? previousState);

    /// <summary>Screen center (crosshair position).</summary>
    Types.Vector2 ScreenCenter { get; }
}
