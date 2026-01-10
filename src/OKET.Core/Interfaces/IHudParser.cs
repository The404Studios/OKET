using OKET.Core.Types;
using OKET.Core.State;

namespace OKET.Core.Interfaces;

/// <summary>
/// Parses HUD elements from frames to extract player state.
/// </summary>
public interface IHudParser
{
    /// <summary>Parse HUD from a frame.</summary>
    HudState Parse(Frame frame);

    /// <summary>Configure HUD regions for the current resolution.</summary>
    void Configure(int screenWidth, int screenHeight);

    /// <summary>Whether OCR is enabled for text extraction.</summary>
    bool UseOcr { get; set; }
}
