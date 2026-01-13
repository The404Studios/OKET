namespace OKET.Core.Telemetry;

/// <summary>
/// Prediction target types - what we're predicting.
/// </summary>
public enum PredictionTarget : byte
{
    /// <summary>Predict entity position.</summary>
    EntityPosition = 1,

    /// <summary>Predict threat proximity (nearest threat distance).</summary>
    ThreatProximity = 2,

    /// <summary>Predict self health change.</summary>
    SelfHealth = 3,

    /// <summary>Predict navigation progress.</summary>
    NavigationProgress = 4,

    /// <summary>Predict entity velocity.</summary>
    EntityVelocity = 5,

    /// <summary>Predict threat count change.</summary>
    ThreatCount = 6
}

/// <summary>
/// Prediction token - agent's prediction for future state.
/// Used for prediction error feedback loop.
/// </summary>
public readonly record struct PredictionToken(
    PredictionTarget Target,
    int TrackId,              // for entity targets, else 0
    float PredA,              // predicted value A (e.g., X position)
    float PredB,              // predicted value B (e.g., Y position)
    float HorizonSeconds      // how far ahead this prediction is
) : ITokenPayload
{
    public static PredictionToken EntityPos(int trackId, float predX, float predY, float horizon = 0.1f) =>
        new(PredictionTarget.EntityPosition, trackId, predX, predY, horizon);

    public static PredictionToken ThreatDist(float predictedDistance, float horizon = 0.1f) =>
        new(PredictionTarget.ThreatProximity, 0, predictedDistance, 0, horizon);

    public static PredictionToken Health(float predictedHealth, float horizon = 1f) =>
        new(PredictionTarget.SelfHealth, 0, predictedHealth, 0, horizon);
}

/// <summary>
/// Error token - measured prediction error.
/// This is the key feedback signal for learning.
/// </summary>
public readonly record struct ErrorToken(
    PredictionTarget Target,
    int TrackId,
    float ErrorMagnitude,     // absolute error (always positive)
    float ErrorA,             // error in dimension A (signed)
    float ErrorB              // error in dimension B (signed)
) : ITokenPayload
{
    /// <summary>Whether this error is considered "high" (needs attention).</summary>
    public bool IsHighError => ErrorMagnitude > 50f;

    /// <summary>Whether predictions are reliable based on this error.</summary>
    public bool IsReliable => ErrorMagnitude < 30f;

    public static ErrorToken FromPrediction(
        PredictionTarget target,
        int trackId,
        float predictedA, float actualA,
        float predictedB, float actualB)
    {
        float errA = actualA - predictedA;
        float errB = actualB - predictedB;
        float magnitude = MathF.Sqrt(errA * errA + errB * errB);
        return new(target, trackId, magnitude, errA, errB);
    }
}

/// <summary>
/// Distance fusion result - combines multiple distance estimates.
/// </summary>
public readonly record struct FusedDistanceEstimate(
    float DistanceMeters,
    float Confidence,
    DistanceFusionSource PrimarySource,
    float AgreementFactor     // how well sources agree [0..1]
)
{
    /// <summary>Whether this estimate should be trusted for decisions.</summary>
    public bool IsTrustworthy => Confidence > 0.5f && AgreementFactor > 0.6f;

    /// <summary>
    /// Fuse two distance estimates with confidence weighting.
    /// </summary>
    public static FusedDistanceEstimate Fuse(
        float distBbox, float confBbox,
        float distOcr, float confOcr)
    {
        const float eps = 0.0001f;

        // Confidence-weighted average
        float wBbox = confBbox / (confBbox + confOcr + eps);
        float wOcr = confOcr / (confBbox + confOcr + eps);
        float fusedDist = wBbox * distBbox + wOcr * distOcr;

        // Agreement factor - drops if distances disagree wildly
        float disagreement = MathF.Abs(distBbox - distOcr) / MathF.Max(distBbox, distOcr + eps);
        float agreementFactor = MathF.Max(0, 1f - disagreement);

        // Final confidence
        float fusedConf = MathF.Max(confBbox, confOcr) * agreementFactor;
        fusedConf = Math.Clamp(fusedConf, 0f, 1f);

        // Determine primary source
        var primary = confBbox >= confOcr ? DistanceFusionSource.BoundingBox : DistanceFusionSource.OCR;

        return new(fusedDist, fusedConf, primary, agreementFactor);
    }
}

/// <summary>
/// Source of fused distance estimate.
/// </summary>
public enum DistanceFusionSource : byte
{
    Unknown = 0,
    BoundingBox = 1,
    OCR = 2,
    Fused = 3,
    GameUI = 4
}
