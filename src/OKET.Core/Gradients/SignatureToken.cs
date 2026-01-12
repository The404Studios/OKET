namespace OKET.Core.Gradients;

/// <summary>
/// Tokenization Layer: Collapse each Gradient Object into a fixed signature.
///
/// PRINCIPLE: Instead of "enemy," the type is structural:
/// - MOVING_COHERENT_FIELD
/// - STATIC_UI_FIELD
/// - FLASH_EVENT
/// - CONTOUR_GATEWAY
/// - TRACKED_TARGETLIKE
///
/// Token = (structural_type + signature_vector + prototype_id + confidence + time)
///
/// Tokens are what the system "parses automatically for each section."
/// This is the bridge between raw perception and semantic understanding.
/// </summary>
public sealed class SignatureToken
{
    private readonly int _tokenId;
    private readonly long _createdFrame;

    // Structural type (what kind of field pattern)
    private FieldType _fieldType;

    // Core signature from gradient object
    private SignatureVector _signature;

    // Prototype matching
    private int _prototypeId = -1;
    private float _prototypeMatchScore;
    private float _prototypeConfidence;

    // Token state
    private float _confidence;
    private float _novelty; // How different from known prototypes
    private int _ageFrames;
    private bool _isStable;
    private string? _resolvedName;

    // Behavioral annotation (from experience)
    private TokenBehavior _observedBehavior;

    public int TokenId => _tokenId;
    public FieldType Type => _fieldType;
    public SignatureVector Signature => _signature;
    public int PrototypeId => _prototypeId;
    public float PrototypeMatch => _prototypeMatchScore;
    public float Confidence => _confidence;
    public float Novelty => _novelty;
    public int AgeFrames => _ageFrames;
    public bool IsStable => _isStable;
    public string? ResolvedName => _resolvedName;
    public TokenBehavior Behavior => _observedBehavior;
    public long CreatedFrame => _createdFrame;

    /// <summary>Has this token been matched to a known prototype?</summary>
    public bool IsKnown => _prototypeId >= 0 && _prototypeMatchScore > 0.6f;

    /// <summary>Is this token novel (not well matched to any prototype)?</summary>
    public bool IsNovel => _novelty > 0.5f;

    /// <summary>Is this token ready to be named (stable + known)?</summary>
    public bool CanBeNamed => _isStable && IsKnown && _resolvedName == null;

    public SignatureToken(int tokenId, long frameId)
    {
        _tokenId = tokenId;
        _createdFrame = frameId;
        _observedBehavior = new TokenBehavior();
    }

    /// <summary>
    /// Update token from a gradient object.
    /// </summary>
    public void UpdateFromObject(GradientObject obj, long frameId)
    {
        _signature = obj.GetSignature();
        _ageFrames = (int)(frameId - _createdFrame);
        _confidence = obj.Confidence;

        // Classify structural type
        _fieldType = ClassifyFieldType(obj);

        // Update stability
        _isStable = obj.IsStable && _ageFrames > 30;

        // Copy prototype info if object has it
        if (obj.HasIdentity)
        {
            _prototypeId = obj.PrototypeId;
            _prototypeMatchScore = obj.PrototypeMatch;
        }

        if (obj.StableName != null)
        {
            _resolvedName = obj.StableName;
        }
    }

    /// <summary>
    /// Classify the structural type of field pattern.
    /// </summary>
    private static FieldType ClassifyFieldType(GradientObject obj)
    {
        var sig = obj.GetSignature();

        // High motion = moving field
        if (sig.Speed > 0.3f)
        {
            // Large + fast = threat-like
            if (sig.Area > 20 && sig.AspectRatio > 0.8f && sig.AspectRatio < 1.5f)
                return FieldType.TrackedTargetlike;

            return FieldType.MovingCoherentField;
        }

        // High saturation + specific position = UI element
        if (sig.Saturation > 0.6f && sig.Stability > 0.8f)
        {
            if (sig.NormalizedY < 0.2f || sig.NormalizedY > 0.8f)
                return FieldType.StaticUIField;
        }

        // Brief existence + high temporal change = flash
        if (sig.AgeFrames < 10 && sig.Confidence < 0.5f)
            return FieldType.FlashEvent;

        // High edge density + elongated = gateway/corridor
        if (sig.EdgeDensity > 0.5f && (sig.AspectRatio < 0.3f || sig.AspectRatio > 3f))
            return FieldType.ContourGateway;

        // Stable + colored + grounded = item-like
        if (sig.Stability > 0.5f && sig.Saturation > 0.3f)
            return FieldType.StableColoredField;

        // Default
        return sig.Speed > 0.1f ? FieldType.MovingCoherentField : FieldType.StaticCoherentField;
    }

    /// <summary>
    /// Set prototype match result.
    /// </summary>
    public void SetPrototypeMatch(int prototypeId, float matchScore, float confidence)
    {
        _prototypeId = prototypeId;
        _prototypeMatchScore = matchScore;
        _prototypeConfidence = confidence;

        // Novelty is inverse of match quality
        _novelty = 1f - matchScore;
    }

    /// <summary>
    /// Set novelty score (when no good prototype match found).
    /// </summary>
    public void SetNovelty(float novelty)
    {
        _novelty = novelty;
    }

    /// <summary>
    /// Assign resolved name after stabilization.
    /// </summary>
    public void ResolveName(string name)
    {
        if (_isStable)
        {
            _resolvedName = name;
        }
    }

    /// <summary>
    /// Update observed behavior from experience.
    /// </summary>
    public void UpdateBehavior(TokenBehavior behavior)
    {
        _observedBehavior = _observedBehavior.MergeWith(behavior);
    }

    /// <summary>
    /// Get compressed representation for memory storage.
    /// </summary>
    public CompressedToken Compress()
    {
        return new CompressedToken
        {
            TokenId = _tokenId,
            Type = _fieldType,
            PrototypeId = _prototypeId,
            MatchScore = _prototypeMatchScore,
            Novelty = _novelty,
            Confidence = _confidence,
            ResolvedName = _resolvedName,
            Behavior = _observedBehavior
        };
    }

    public override string ToString()
    {
        string identity = _resolvedName ?? (_prototypeId >= 0 ? $"Proto#{_prototypeId}" : "Unknown");
        return $"Token[{_tokenId}]: {_fieldType} → {identity} " +
               $"match={_prototypeMatchScore:F2} novelty={_novelty:F2} " +
               $"stable={_isStable} age={_ageFrames}";
    }
}

/// <summary>
/// Structural classification of field patterns.
/// NOT semantic (no "enemy", "item") - just structural.
/// </summary>
public enum FieldType
{
    /// <summary>Unclassified coherent field.</summary>
    Unknown,

    /// <summary>Moving coherent region (anything that moves consistently).</summary>
    MovingCoherentField,

    /// <summary>Static coherent region.</summary>
    StaticCoherentField,

    /// <summary>Static UI element (HUD, menu, etc.).</summary>
    StaticUIField,

    /// <summary>Brief flash or transient event.</summary>
    FlashEvent,

    /// <summary>Elongated edge structure (doorway, corridor, wall).</summary>
    ContourGateway,

    /// <summary>Looks like a trackable target (moving, sized, shaped).</summary>
    TrackedTargetlike,

    /// <summary>Stable colored region (potentially interactable).</summary>
    StableColoredField,

    /// <summary>Background/environmental field.</summary>
    EnvironmentalField
}

/// <summary>
/// Observed behavior patterns for a token type.
/// Learned from experience, not predefined.
/// </summary>
public struct TokenBehavior
{
    /// <summary>Does this typically approach the player?</summary>
    public float ApproachTendency { get; set; }

    /// <summary>Does this typically cause damage?</summary>
    public float DamageTendency { get; set; }

    /// <summary>Is this typically collectible/beneficial?</summary>
    public float BenefitTendency { get; set; }

    /// <summary>Does this block movement?</summary>
    public float ObstacleTendency { get; set; }

    /// <summary>How predictable is this token's behavior?</summary>
    public float Predictability { get; set; }

    /// <summary>How many times have we encountered this?</summary>
    public int EncounterCount { get; set; }

    /// <summary>Merge with another behavior observation.</summary>
    public TokenBehavior MergeWith(TokenBehavior other)
    {
        float total = EncounterCount + other.EncounterCount;
        if (total == 0) return this;

        float w1 = EncounterCount / total;
        float w2 = other.EncounterCount / total;

        return new TokenBehavior
        {
            ApproachTendency = ApproachTendency * w1 + other.ApproachTendency * w2,
            DamageTendency = DamageTendency * w1 + other.DamageTendency * w2,
            BenefitTendency = BenefitTendency * w1 + other.BenefitTendency * w2,
            ObstacleTendency = ObstacleTendency * w1 + other.ObstacleTendency * w2,
            Predictability = Predictability * w1 + other.Predictability * w2,
            EncounterCount = EncounterCount + other.EncounterCount
        };
    }
}

/// <summary>
/// Compressed token for memory storage.
/// </summary>
public readonly struct CompressedToken
{
    public int TokenId { get; init; }
    public FieldType Type { get; init; }
    public int PrototypeId { get; init; }
    public float MatchScore { get; init; }
    public float Novelty { get; init; }
    public float Confidence { get; init; }
    public string? ResolvedName { get; init; }
    public TokenBehavior Behavior { get; init; }
}
