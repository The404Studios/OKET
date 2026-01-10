namespace OKET.Core.Integration;

using OKET.Core.Cognition;
using OKET.Core.Normalization;

/// <summary>
/// Bridges the FrameIntegrator to existing cognitive components.
///
/// Maps:
/// - ZScoreStack → Frame states
/// - InteroceptiveState → Feeling/control signals
/// - ExpectationGaps → Gap pressure
///
/// The bridge ensures the CENTER sees everything through proper frame mapping.
/// </summary>
public sealed class IntegrationBridge
{
    private readonly FrameIntegrator _integrator;
    private readonly ZScoreStack _zStack;

    // Cache for computing deltas
    private float _lastZ0;
    private float _lastZ1;
    private float _lastZ4;

    public IntegrationBridge(FrameIntegrator integrator, ZScoreStack zStack)
    {
        _integrator = integrator;
        _zStack = zStack;
    }

    /// <summary>
    /// Update the integrator from current cognitive state.
    /// Call this every frame to keep the center synchronized.
    /// </summary>
    public IntegrationState Update(
        InteroceptiveState feeling,
        float gapPressure,
        float predictionConfidence,
        float patternMatch,
        float immediacy = 0f)
    {
        // Get current Z values
        float z0 = _zStack.GetLevel(0)?.Current ?? 0f;
        float z1 = _zStack.GetLevel(1)?.Current ?? 0f;
        float z2 = _zStack.GetLevel(2)?.Current ?? 0f;
        float z3 = _zStack.GetLevel(3)?.Current ?? 0f;
        float z4 = _zStack.GetLevel(4)?.Current ?? 0f;

        // Map to local frame (right brain)
        _integrator.UpdateLocalFrame(
            perceptionStrain: z0,                          // Z₀ = perception load
            feelingValence: feeling.OutcomeTrend,          // How things feel
            salienceLevel: 1f - feeling.SystemStrain,      // High strain = low salience
            immediacy: immediacy,                          // Urgency
            gapPressure: gapPressure                       // Expectation gaps
        );

        // Map to global frame (left brain)
        _integrator.UpdateGlobalFrame(
            predictionConfidence: predictionConfidence,
            patternMatch: patternMatch,
            temporalStability: 1f - Math.Abs(z3),          // Z₃ trend stability
            compressionQuality: feeling.ControlConfidence, // How well model fits
            inheritedLoad: z2                              // Current load on system
        );

        // Integrate
        var state = _integrator.Integrate();

        // Update cache
        _lastZ0 = z0;
        _lastZ1 = z1;
        _lastZ4 = z4;

        return state;
    }

    /// <summary>
    /// Set directional bias based on belief candidate.
    /// </summary>
    public void SetDirection(float magnitude, float alignment, float novelty)
    {
        _integrator.SetDirectionalBias(magnitude, alignment, novelty);
    }

    /// <summary>
    /// Get the current permission level.
    /// Use this to gate actions.
    /// </summary>
    public float GetPermission() => _integrator.Permission;

    /// <summary>
    /// Get modulation signal for perception systems (right brain).
    /// </summary>
    public float GetLocalModulation() => _integrator.GetState().LocalModulation;

    /// <summary>
    /// Get modulation signal for prediction systems (left brain).
    /// </summary>
    public float GetGlobalModulation() => _integrator.GetState().GlobalModulation;

    /// <summary>
    /// Check if action should be inhibited.
    /// </summary>
    public bool ShouldInhibit(float threshold = 0.3f)
    {
        return _integrator.Permission < threshold ||
               _integrator.TransformationStrain > 0.7f;
    }

    /// <summary>
    /// Check if frames are sufficiently synchronized.
    /// </summary>
    public bool IsSynchronized(float threshold = 0.5f)
    {
        return _integrator.Coherence > threshold &&
               _integrator.TransformationStrain < 0.5f;
    }

    /// <summary>
    /// Get comprehensive diagnostics.
    /// </summary>
    public string GetDiagnostics()
    {
        return _integrator.GetDiagnostics();
    }
}

/// <summary>
/// Extension methods for cognitive pipeline integration.
/// </summary>
public static class IntegrationExtensions
{
    /// <summary>
    /// Map Z-score agreement to frame agreement signal.
    /// Z₁ is already multimodal agreement - use it directly.
    /// </summary>
    public static float ToFrameAgreement(this float z1)
    {
        // Z₁ in [-3, 3] maps to agreement [0, 1]
        // Negative Z₁ = disagreement
        // Positive Z₁ = agreement
        return Math.Clamp((z1 + 1.5f) / 3f, 0f, 1f);
    }

    /// <summary>
    /// Map Z-score strain to transformation strain.
    /// Higher Z = more strain on the transformation.
    /// </summary>
    public static float ToTransformationStrain(this float z4)
    {
        // Z₄ in [0, 3+] maps to strain [0, 1]
        return Math.Clamp(z4 / 2f, 0f, 1f);
    }

    /// <summary>
    /// Map feeling valence to directional alignment.
    /// Positive feeling = aligned with current direction.
    /// </summary>
    public static float ToDirectionalAlignment(this float outcomeTrend)
    {
        // Already in [-1, 1]
        return Math.Clamp(outcomeTrend, -1f, 1f);
    }
}
