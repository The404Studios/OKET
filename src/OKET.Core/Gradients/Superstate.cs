namespace OKET.Core.Gradients;

/// <summary>
/// Global Layer: Superstate - Situations, not just objects.
///
/// PRINCIPLE: A Superstate is a graph of tokens + relations.
/// It represents "what's happening" not just "what's there."
///
/// This is the "global that houses regional interpretation for local nodes."
///
/// Superstates enable:
/// - Recognition of situations ("ThreatClosingLeft", "LootCluster", "OpenLane")
/// - Pattern-based action authorization
/// - Memory of similar situations and their outcomes
/// </summary>
public sealed class Superstate
{
    private readonly int _superstateId;
    private readonly long _createdFrame;
    private readonly List<TokenNode> _nodes = new();
    private readonly List<TokenRelation> _relations = new();

    // Situation classification
    private SuperstateType _type;
    private float _urgency;
    private float _opportunity;
    private float _stability;
    private float _confidence;

    // Temporal tracking
    private SuperstateSignature _signature;
    private SuperstateSignature? _prevSignature;
    private float _signatureDrift;
    private int _ageFrames;

    // Pattern matching
    private int _matchedPatternId = -1;
    private float _patternMatchScore;
    private string? _situationName;

    public int SuperstateId => _superstateId;
    public IReadOnlyList<TokenNode> Nodes => _nodes;
    public IReadOnlyList<TokenRelation> Relations => _relations;
    public SuperstateType Type => _type;
    public float Urgency => _urgency;
    public float Opportunity => _opportunity;
    public float Stability => _stability;
    public float Confidence => _confidence;
    public int AgeFrames => _ageFrames;
    public int MatchedPatternId => _matchedPatternId;
    public float PatternMatchScore => _patternMatchScore;
    public string? SituationName => _situationName;
    public SuperstateSignature Signature => _signature;

    /// <summary>Is this a recognized situation?</summary>
    public bool IsRecognized => _matchedPatternId >= 0 && _patternMatchScore > 0.5f;

    /// <summary>Is action required?</summary>
    public bool RequiresAction => _urgency > 0.5f || _type == SuperstateType.ThreatApproaching;

    /// <summary>Is this situation stable (not changing rapidly)?</summary>
    public bool IsStable => _stability > 0.6f && _signatureDrift < 0.2f;

    public Superstate(int superstateId, long frameId)
    {
        _superstateId = superstateId;
        _createdFrame = frameId;
    }

    /// <summary>
    /// Build superstate from current tokens.
    /// </summary>
    public void BuildFromTokens(IEnumerable<SignatureToken> tokens, long frameId)
    {
        _ageFrames = (int)(frameId - _createdFrame);
        _nodes.Clear();
        _relations.Clear();

        // Add all tokens as nodes
        foreach (var token in tokens)
        {
            _nodes.Add(new TokenNode
            {
                TokenId = token.TokenId,
                Type = token.Type,
                Signature = token.Signature,
                Confidence = token.Confidence,
                IsNovel = token.IsNovel,
                Behavior = token.Behavior
            });
        }

        // Compute relations between nodes
        ComputeRelations();

        // Classify situation
        ClassifySituation();

        // Compute signature
        _prevSignature = _signature;
        _signature = ComputeSignature();

        // Track signature drift
        if (_prevSignature.HasValue)
        {
            _signatureDrift = _signature.DistanceTo(_prevSignature.Value);
        }

        // Update stability
        _stability = 1f / (1f + _signatureDrift + _nodes.Count(n => n.IsNovel) * 0.2f);
    }

    private void ComputeRelations()
    {
        for (int i = 0; i < _nodes.Count; i++)
        {
            for (int j = i + 1; j < _nodes.Count; j++)
            {
                var nodeA = _nodes[i];
                var nodeB = _nodes[j];

                // Spatial relation
                float dx = nodeB.Signature.NormalizedX - nodeA.Signature.NormalizedX;
                float dy = nodeB.Signature.NormalizedY - nodeA.Signature.NormalizedY;
                float distance = MathF.Sqrt(dx * dx + dy * dy);

                // Motion relation
                float relVelX = nodeB.Signature.VelocityX - nodeA.Signature.VelocityX;
                float relVelY = nodeB.Signature.VelocityY - nodeA.Signature.VelocityY;
                float relSpeed = MathF.Sqrt(relVelX * relVelX + relVelY * relVelY);

                // Approaching/receding
                float dotProduct = dx * relVelX + dy * relVelY;
                bool approaching = dotProduct < 0;

                // Only record significant relations
                if (distance < 0.5f || relSpeed > 0.1f)
                {
                    _relations.Add(new TokenRelation
                    {
                        TokenIdA = nodeA.TokenId,
                        TokenIdB = nodeB.TokenId,
                        Distance = distance,
                        RelativeVelocity = relSpeed,
                        IsApproaching = approaching,
                        DirectionX = dx,
                        DirectionY = dy
                    });
                }
            }
        }
    }

    private void ClassifySituation()
    {
        // Count different field types
        int threatLikeCount = _nodes.Count(n => n.Type == FieldType.TrackedTargetlike);
        int movingCount = _nodes.Count(n => n.Type == FieldType.MovingCoherentField);
        int itemLikeCount = _nodes.Count(n => n.Type == FieldType.StableColoredField);
        int uiCount = _nodes.Count(n => n.Type == FieldType.StaticUIField);

        // Check for approaching threats
        bool hasApproachingThreat = _relations.Any(r =>
            r.IsApproaching &&
            _nodes.Any(n => n.TokenId == r.TokenIdA && n.Type == FieldType.TrackedTargetlike));

        // Compute urgency from threat-like nodes
        _urgency = 0;
        foreach (var node in _nodes.Where(n => n.Type == FieldType.TrackedTargetlike))
        {
            float proximity = 1f - node.Signature.NormalizedY; // Closer to bottom = closer to player
            float speed = node.Signature.Speed;
            _urgency = Math.Max(_urgency, proximity * 0.6f + speed * 0.4f);
        }

        // Compute opportunity from item-like nodes
        _opportunity = 0;
        foreach (var node in _nodes.Where(n => n.Type == FieldType.StableColoredField))
        {
            _opportunity = Math.Max(_opportunity, node.Confidence * node.Behavior.BenefitTendency);
        }

        // Classify type
        if (hasApproachingThreat && _urgency > 0.5f)
        {
            _type = SuperstateType.ThreatApproaching;
        }
        else if (threatLikeCount > 2)
        {
            _type = SuperstateType.MultiThreat;
        }
        else if (threatLikeCount > 0 && _urgency > 0.3f)
        {
            _type = SuperstateType.ThreatPresent;
        }
        else if (itemLikeCount > 1)
        {
            _type = SuperstateType.OpportunityCluster;
        }
        else if (movingCount == 0 && threatLikeCount == 0)
        {
            _type = SuperstateType.Clear;
        }
        else if (_stability > 0.7f && _urgency < 0.2f)
        {
            _type = SuperstateType.Stable;
        }
        else
        {
            _type = SuperstateType.Neutral;
        }

        // Confidence from node confidences
        _confidence = _nodes.Count > 0 ? _nodes.Average(n => n.Confidence) : 0;
    }

    private SuperstateSignature ComputeSignature()
    {
        return new SuperstateSignature
        {
            NodeCount = _nodes.Count,
            RelationCount = _relations.Count,
            ThreatLikeCount = _nodes.Count(n => n.Type == FieldType.TrackedTargetlike),
            MovingCount = _nodes.Count(n => n.Signature.Speed > 0.1f),
            StaticCount = _nodes.Count(n => n.Signature.Speed <= 0.1f),
            Urgency = _urgency,
            Opportunity = _opportunity,
            MeanConfidence = _confidence,
            ApproachingCount = _relations.Count(r => r.IsApproaching),
            TotalRelativeVelocity = _relations.Sum(r => r.RelativeVelocity),
            Type = _type
        };
    }

    /// <summary>
    /// Set pattern match result.
    /// </summary>
    public void SetPatternMatch(int patternId, float matchScore, string? name)
    {
        _matchedPatternId = patternId;
        _patternMatchScore = matchScore;
        _situationName = name;
    }

    /// <summary>
    /// Get situation summary for action authorization.
    /// </summary>
    public SituationSummary GetSummary()
    {
        return new SituationSummary
        {
            Type = _type,
            Urgency = _urgency,
            Opportunity = _opportunity,
            Confidence = _confidence,
            Stability = _stability,
            ThreatCount = _nodes.Count(n => n.Type == FieldType.TrackedTargetlike),
            ItemCount = _nodes.Count(n => n.Type == FieldType.StableColoredField),
            IsApproachingThreat = _relations.Any(r => r.IsApproaching &&
                _nodes.Any(n => n.TokenId == r.TokenIdA && n.Type == FieldType.TrackedTargetlike)),
            IsRecognized = IsRecognized,
            SituationName = _situationName
        };
    }

    public override string ToString()
    {
        string name = _situationName ?? $"Pattern#{_matchedPatternId}";
        return $"Superstate[{_superstateId}]: {_type} ({name}) " +
               $"nodes={_nodes.Count} relations={_relations.Count} " +
               $"urgency={_urgency:F2} opp={_opportunity:F2} " +
               $"stable={_stability:F2} conf={_confidence:F2}";
    }
}

/// <summary>
/// Node in the superstate graph (represents a token).
/// </summary>
public readonly struct TokenNode
{
    public int TokenId { get; init; }
    public FieldType Type { get; init; }
    public SignatureVector Signature { get; init; }
    public float Confidence { get; init; }
    public bool IsNovel { get; init; }
    public TokenBehavior Behavior { get; init; }
}

/// <summary>
/// Relation between two tokens in the superstate.
/// </summary>
public readonly struct TokenRelation
{
    public int TokenIdA { get; init; }
    public int TokenIdB { get; init; }
    public float Distance { get; init; }
    public float RelativeVelocity { get; init; }
    public bool IsApproaching { get; init; }
    public float DirectionX { get; init; }
    public float DirectionY { get; init; }
}

/// <summary>
/// High-level classification of the situation.
/// </summary>
public enum SuperstateType
{
    /// <summary>Unknown/unclassified situation.</summary>
    Unknown,

    /// <summary>No significant activity.</summary>
    Clear,

    /// <summary>Stable, non-threatening situation.</summary>
    Stable,

    /// <summary>Normal activity, nothing urgent.</summary>
    Neutral,

    /// <summary>Threat-like objects present but not urgent.</summary>
    ThreatPresent,

    /// <summary>Threat actively approaching - action required.</summary>
    ThreatApproaching,

    /// <summary>Multiple threats present.</summary>
    MultiThreat,

    /// <summary>Multiple opportunities (items, resources) clustered.</summary>
    OpportunityCluster,

    /// <summary>Transitional state (changing rapidly).</summary>
    Transitional
}

/// <summary>
/// Fixed-length signature for superstate comparison and memory.
/// </summary>
public readonly struct SuperstateSignature
{
    public int NodeCount { get; init; }
    public int RelationCount { get; init; }
    public int ThreatLikeCount { get; init; }
    public int MovingCount { get; init; }
    public int StaticCount { get; init; }
    public float Urgency { get; init; }
    public float Opportunity { get; init; }
    public float MeanConfidence { get; init; }
    public int ApproachingCount { get; init; }
    public float TotalRelativeVelocity { get; init; }
    public SuperstateType Type { get; init; }

    /// <summary>
    /// Distance to another signature.
    /// </summary>
    public float DistanceTo(SuperstateSignature other)
    {
        float sum = 0;
        sum += Math.Abs(NodeCount - other.NodeCount) * 0.1f;
        sum += Math.Abs(ThreatLikeCount - other.ThreatLikeCount) * 0.3f;
        sum += Math.Abs(Urgency - other.Urgency);
        sum += Math.Abs(Opportunity - other.Opportunity);
        sum += Math.Abs(ApproachingCount - other.ApproachingCount) * 0.2f;
        sum += Type != other.Type ? 0.5f : 0f;
        return sum;
    }

    /// <summary>
    /// Similarity to another signature (0-1).
    /// </summary>
    public float SimilarityTo(SuperstateSignature other)
    {
        return 1f / (1f + DistanceTo(other));
    }
}

/// <summary>
/// Summary of situation for action decisions.
/// </summary>
public readonly struct SituationSummary
{
    public SuperstateType Type { get; init; }
    public float Urgency { get; init; }
    public float Opportunity { get; init; }
    public float Confidence { get; init; }
    public float Stability { get; init; }
    public int ThreatCount { get; init; }
    public int ItemCount { get; init; }
    public bool IsApproachingThreat { get; init; }
    public bool IsRecognized { get; init; }
    public string? SituationName { get; init; }
}
