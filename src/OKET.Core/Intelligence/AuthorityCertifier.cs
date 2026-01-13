namespace OKET.Core.Intelligence;

/// <summary>
/// Authority Certifier - Validates and certifies detections.
///
/// PRINCIPLE: Trust is earned, not given.
///
/// Every detection starts at TrustLevel.Unknown.
/// Through consistent behavior and outcome validation,
/// detections can be certified to higher trust levels.
///
/// Trust hierarchy:
/// - Unknown: New, unverified detection
/// - Provisional: Seen multiple times, behavior noted
/// - Certified: Behavior matches known pattern
/// - Trusted: High confidence from multiple validations
/// - Absolute: Hardcoded ground truth (root certificates)
///
/// This ensures the system doesn't blindly trust classifications.
/// </summary>
public sealed class AuthorityCertifier
{
    // Certification patterns (learned behaviors)
    private readonly Dictionary<string, CertificationPattern> _patterns = new();

    // Root certificates (hardcoded truths)
    private readonly List<RootCertificate> _rootCerts = new();

    // Certification history for learning
    private readonly Dictionary<int, CertificationHistory> _trackHistory = new();

    // Configuration
    private readonly CertifierConfig _config;

    public AuthorityCertifier(CertifierConfig? config = null)
    {
        _config = config ?? CertifierConfig.Default;
        InitializeRootCertificates();
        InitializePatterns();
    }

    /// <summary>
    /// Certify a detection based on its properties and history.
    /// </summary>
    public AuthorityCertification Certify(
        IntelligentDetection detection,
        GameState? state)
    {
        // Check root certificates first (absolute trust)
        var rootCert = CheckRootCertificates(detection, state);
        if (rootCert != null)
            return rootCert;

        // Get or create history for this track
        if (!_trackHistory.TryGetValue(detection.TrackId, out var history))
        {
            history = new CertificationHistory(detection.TrackId);
            _trackHistory[detection.TrackId] = history;
        }

        // Update history
        history.Update(detection);

        // Check for pattern matches
        var patternMatch = MatchPattern(detection, history);
        if (patternMatch != null)
        {
            return CreateCertification(patternMatch, history);
        }

        // Check for behavioral certification
        if (history.ObservationCount >= _config.MinObservationsForCertification)
        {
            return CertifyFromBehavior(detection, history);
        }

        // Default: provisional or unknown
        return history.ObservationCount >= _config.MinObservationsForProvisional
            ? CreateProvisionalCertification(detection, history)
            : AuthorityCertification.Unknown;
    }

    /// <summary>
    /// Record outcome for learning.
    /// </summary>
    public void RecordOutcome(ActionOutcome outcome)
    {
        if (!_trackHistory.TryGetValue((int)outcome.DetectionId, out var history))
            return;

        // Update history with outcome
        history.RecordOutcome(outcome);

        // Update pattern confidence
        var matchedPattern = _patterns.Values
            .FirstOrDefault(p => p.MatchesDetection(history.LastDetection));

        if (matchedPattern != null)
        {
            matchedPattern.RecordOutcome(outcome);
        }

        // Potentially create new pattern from consistent behavior
        if (history.HasConsistentBehavior && history.OutcomeCount >= _config.MinOutcomesForPattern)
        {
            CreateOrUpdatePattern(history);
        }
    }

    /// <summary>
    /// Check root certificates (hardcoded truths).
    /// </summary>
    private AuthorityCertification? CheckRootCertificates(
        IntelligentDetection detection,
        GameState? state)
    {
        foreach (var cert in _rootCerts)
        {
            if (cert.Matches(detection, state))
            {
                return new AuthorityCertification
                {
                    Level = TrustLevel.Absolute,
                    CertifiedClass = cert.CertifiedClass,
                    TrustScore = 1f,
                    ThreatModifier = cert.ThreatModifier,
                    OpportunityModifier = cert.OpportunityModifier,
                    BaseThreat = cert.BaseThreat,
                    BaseOpportunity = cert.BaseOpportunity,
                    CertificationReason = cert.Reason
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Match against known patterns.
    /// </summary>
    private CertificationPattern? MatchPattern(
        IntelligentDetection detection,
        CertificationHistory history)
    {
        CertificationPattern? best = null;
        float bestScore = 0;

        foreach (var pattern in _patterns.Values)
        {
            float score = pattern.MatchScore(detection, history);
            if (score > bestScore && score >= _config.PatternMatchThreshold)
            {
                bestScore = score;
                best = pattern;
            }
        }

        return best;
    }

    /// <summary>
    /// Create certification from pattern match.
    /// </summary>
    private AuthorityCertification CreateCertification(
        CertificationPattern pattern,
        CertificationHistory history)
    {
        var trustLevel = pattern.Confidence switch
        {
            >= 0.9f => TrustLevel.Trusted,
            >= 0.7f => TrustLevel.Certified,
            >= 0.5f => TrustLevel.Provisional,
            _ => TrustLevel.Unknown
        };

        return new AuthorityCertification
        {
            Level = trustLevel,
            CertifiedClass = pattern.CertifiedClass,
            TrustScore = pattern.Confidence,
            ThreatModifier = pattern.IsThreat ? 1.2f : 0.8f,
            OpportunityModifier = pattern.IsOpportunity ? 1.2f : 0.8f,
            BaseThreat = pattern.BaseThreatScore,
            BaseOpportunity = pattern.BaseOpportunityScore,
            CertificationReason = $"Pattern: {pattern.Name} ({pattern.Confidence:P0})"
        };
    }

    /// <summary>
    /// Certify from observed behavior.
    /// </summary>
    private AuthorityCertification CertifyFromBehavior(
        IntelligentDetection detection,
        CertificationHistory history)
    {
        // Analyze behavior patterns
        bool isConsistentlyMoving = history.AverageSpeed > 0.5f;
        bool isApproaching = history.ApproachRate > 0.5f;
        bool causedDamage = history.DamageCaused > 0;
        bool gaveBenefit = history.BenefitGiven > 0;

        float threatScore = 0;
        float opportunityScore = 0;
        string? certClass = null;

        // Threat behavior: moves towards player + caused damage
        if (isConsistentlyMoving && isApproaching && causedDamage)
        {
            threatScore = 0.8f;
            certClass = "Threat";
        }
        // Opportunity behavior: static + gave benefit
        else if (!isConsistentlyMoving && gaveBenefit)
        {
            opportunityScore = 0.8f;
            certClass = "Resource";
        }

        var trustLevel = history.Consistency > 0.7f
            ? TrustLevel.Certified
            : TrustLevel.Provisional;

        return new AuthorityCertification
        {
            Level = trustLevel,
            CertifiedClass = certClass,
            TrustScore = history.Consistency,
            ThreatModifier = 1f,
            OpportunityModifier = 1f,
            BaseThreat = threatScore * history.Consistency,
            BaseOpportunity = opportunityScore * history.Consistency,
            CertificationReason = $"Behavior analysis ({history.ObservationCount} observations)"
        };
    }

    /// <summary>
    /// Create provisional certification.
    /// </summary>
    private AuthorityCertification CreateProvisionalCertification(
        IntelligentDetection detection,
        CertificationHistory history)
    {
        // Basic classification from appearance
        bool likelyThreat = detection.IsMoving && detection.Speed > 0.5f;
        bool likelyItem = !detection.IsMoving && detection.Class.IsItem();

        return new AuthorityCertification
        {
            Level = TrustLevel.Provisional,
            CertifiedClass = null, // Not confident enough
            TrustScore = 0.3f + history.ObservationCount * 0.05f,
            ThreatModifier = likelyThreat ? 1.1f : 0.9f,
            OpportunityModifier = likelyItem ? 1.1f : 0.9f,
            BaseThreat = 0,
            BaseOpportunity = 0,
            CertificationReason = $"Provisional ({history.ObservationCount} observations)"
        };
    }

    /// <summary>
    /// Create or update pattern from consistent history.
    /// </summary>
    private void CreateOrUpdatePattern(CertificationHistory history)
    {
        string patternKey = history.GetBehaviorFingerprint();

        if (_patterns.TryGetValue(patternKey, out var existing))
        {
            existing.AddEvidence(history);
        }
        else
        {
            _patterns[patternKey] = CertificationPattern.FromHistory(history);
        }
    }

    /// <summary>
    /// Initialize root certificates (hardcoded truths).
    /// </summary>
    private void InitializeRootCertificates()
    {
        // Fast-moving red object = definite threat
        _rootCerts.Add(new RootCertificate
        {
            Name = "FastRedThreat",
            CertifiedClass = "FastZombie",
            Predicate = (d, s) =>
                d.Speed > 2f &&
                d.IsApproaching &&
                d.RenderColor.R > 200 && d.RenderColor.G < 100,
            ThreatModifier = 1.5f,
            BaseThreat = 0.9f,
            Reason = "Fast approaching red object"
        });

        // Green health pack = definite opportunity
        _rootCerts.Add(new RootCertificate
        {
            Name = "GreenHealthPack",
            CertifiedClass = "HealthKit",
            Predicate = (d, s) =>
                !d.IsMoving &&
                d.RenderColor.G > 200 && d.RenderColor.R < 100 &&
                d.Area < 10000,
            OpportunityModifier = 1.5f,
            BaseOpportunity = 0.9f,
            Reason = "Static green object (health)"
        });

        // Yellow ammo box = opportunity
        _rootCerts.Add(new RootCertificate
        {
            Name = "YellowAmmo",
            CertifiedClass = "AmmoBox",
            Predicate = (d, s) =>
                !d.IsMoving &&
                d.RenderColor.R > 200 && d.RenderColor.G > 150 &&
                d.Area < 8000,
            OpportunityModifier = 1.3f,
            BaseOpportunity = 0.8f,
            Reason = "Static yellow object (ammo)"
        });
    }

    /// <summary>
    /// Initialize known patterns.
    /// </summary>
    private void InitializePatterns()
    {
        // Zombie pattern
        _patterns["zombie_standard"] = new CertificationPattern
        {
            Name = "Standard Zombie",
            CertifiedClass = "Zombie",
            SpeedRange = (0.3f, 2f),
            AspectRatioRange = (0.5f, 2f),
            IsMoving = true,
            IsThreat = true,
            BaseThreatScore = 0.7f,
            Confidence = 0.8f
        };

        // Item pattern
        _patterns["item_standard"] = new CertificationPattern
        {
            Name = "Standard Item",
            CertifiedClass = "Loot",
            SpeedRange = (0f, 0.1f),
            AspectRatioRange = (0.5f, 2f),
            IsMoving = false,
            IsOpportunity = true,
            BaseOpportunityScore = 0.6f,
            Confidence = 0.7f
        };
    }
}

/// <summary>
/// Certifier configuration.
/// </summary>
public sealed class CertifierConfig
{
    public int MinObservationsForProvisional { get; init; } = 3;
    public int MinObservationsForCertification { get; init; } = 10;
    public int MinOutcomesForPattern { get; init; } = 5;
    public float PatternMatchThreshold { get; init; } = 0.6f;

    public static CertifierConfig Default => new();
}

/// <summary>
/// History of a tracked detection for certification.
/// </summary>
internal sealed class CertificationHistory
{
    public int TrackId { get; }
    public int ObservationCount { get; private set; }
    public int OutcomeCount { get; private set; }
    public float AverageSpeed { get; private set; }
    public float ApproachRate { get; private set; }
    public float DamageCaused { get; private set; }
    public float BenefitGiven { get; private set; }
    public float Consistency { get; private set; }
    public bool HasConsistentBehavior => Consistency > 0.6f;
    public IntelligentDetection? LastDetection { get; private set; }

    private readonly Queue<float> _speeds = new();
    private readonly Queue<float> _approaches = new();
    private const int MaxHistory = 30;

    public CertificationHistory(int trackId)
    {
        TrackId = trackId;
        Consistency = 0.5f;
    }

    public void Update(IntelligentDetection detection)
    {
        ObservationCount++;
        LastDetection = detection;

        // Track speed
        _speeds.Enqueue(detection.Speed);
        while (_speeds.Count > MaxHistory) _speeds.Dequeue();
        AverageSpeed = _speeds.Average();

        // Track approach behavior
        float approach = detection.IsApproaching ? 1f : 0f;
        _approaches.Enqueue(approach);
        while (_approaches.Count > MaxHistory) _approaches.Dequeue();
        ApproachRate = _approaches.Average();

        // Update consistency (how stable is the behavior)
        if (ObservationCount > 5)
        {
            float speedVariance = _speeds.Select(s => (s - AverageSpeed) * (s - AverageSpeed)).Average();
            Consistency = 1f / (1f + speedVariance);
        }
    }

    public void RecordOutcome(ActionOutcome outcome)
    {
        OutcomeCount++;

        if (outcome.DamageDealt > 0 || outcome.DamageTaken > 0)
        {
            DamageCaused = DamageCaused * 0.9f + outcome.DamageTaken * 0.1f;
        }

        if (outcome.ItemCollected)
        {
            BenefitGiven = BenefitGiven * 0.9f + 0.1f;
        }
    }

    public string GetBehaviorFingerprint()
    {
        // Create fingerprint from behavior
        string speedClass = AverageSpeed switch
        {
            < 0.3f => "static",
            < 1f => "slow",
            < 2f => "medium",
            _ => "fast"
        };

        string approachClass = ApproachRate > 0.5f ? "approaching" : "passive";
        string threatClass = DamageCaused > 0.3f ? "harmful" : BenefitGiven > 0.3f ? "beneficial" : "neutral";

        return $"{speedClass}_{approachClass}_{threatClass}";
    }
}

/// <summary>
/// Root certificate (hardcoded trust).
/// </summary>
internal sealed class RootCertificate
{
    public string Name { get; init; } = "";
    public string CertifiedClass { get; init; } = "";
    public Func<IntelligentDetection, GameState?, bool> Predicate { get; init; } = (_, _) => false;
    public float ThreatModifier { get; init; } = 1f;
    public float OpportunityModifier { get; init; } = 1f;
    public float BaseThreat { get; init; }
    public float BaseOpportunity { get; init; }
    public string Reason { get; init; } = "";

    public bool Matches(IntelligentDetection detection, GameState? state)
    {
        return Predicate(detection, state);
    }
}

/// <summary>
/// Certification pattern (learned behavior).
/// </summary>
internal sealed class CertificationPattern
{
    public string Name { get; init; } = "";
    public string CertifiedClass { get; init; } = "";
    public (float min, float max) SpeedRange { get; init; }
    public (float min, float max) AspectRatioRange { get; init; }
    public bool IsMoving { get; init; }
    public bool IsThreat { get; init; }
    public bool IsOpportunity { get; init; }
    public float BaseThreatScore { get; init; }
    public float BaseOpportunityScore { get; init; }
    public float Confidence { get; set; }

    private int _matchCount;
    private int _successCount;

    public float MatchScore(IntelligentDetection detection, CertificationHistory history)
    {
        float score = 0;
        int checks = 0;

        // Speed check
        if (detection.Speed >= SpeedRange.min && detection.Speed <= SpeedRange.max)
            score += 1;
        checks++;

        // Movement check
        if (detection.IsMoving == IsMoving)
            score += 1;
        checks++;

        // Aspect ratio check
        float ar = detection.BoundingBox.Height / Math.Max(1, detection.BoundingBox.Width);
        if (ar >= AspectRatioRange.min && ar <= AspectRatioRange.max)
            score += 1;
        checks++;

        return score / checks;
    }

    public bool MatchesDetection(IntelligentDetection? detection)
    {
        if (detection == null) return false;
        return MatchScore(detection, new CertificationHistory(0)) > 0.6f;
    }

    public void RecordOutcome(ActionOutcome outcome)
    {
        _matchCount++;
        if (outcome.Success > 0)
            _successCount++;

        // Update confidence
        if (_matchCount > 5)
        {
            float successRate = (float)_successCount / _matchCount;
            Confidence = Confidence * 0.9f + successRate * 0.1f;
        }
    }

    public void AddEvidence(CertificationHistory history)
    {
        _matchCount++;
        // Confidence naturally increases with more evidence
        Confidence = Math.Min(0.95f, Confidence + 0.01f);
    }

    public static CertificationPattern FromHistory(CertificationHistory history)
    {
        return new CertificationPattern
        {
            Name = $"Learned_{history.TrackId}",
            CertifiedClass = "Learned",
            SpeedRange = (history.AverageSpeed * 0.5f, history.AverageSpeed * 1.5f),
            IsMoving = history.AverageSpeed > 0.3f,
            IsThreat = history.DamageCaused > 0.3f,
            IsOpportunity = history.BenefitGiven > 0.3f,
            BaseThreatScore = history.DamageCaused,
            BaseOpportunityScore = history.BenefitGiven,
            Confidence = history.Consistency
        };
    }
}
