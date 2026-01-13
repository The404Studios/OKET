namespace OKET.Core.Intelligence;

/// <summary>
/// Knowledge Extractor - Extracts learnable patterns from detections.
///
/// PRINCIPLE: Gradient descent in real-time, refactored into knowledge and tags.
///
/// Every frame we observe:
/// - What objects exist
/// - How they behave
/// - What outcomes they produce
///
/// From these observations, we extract:
/// - Tags: Semantic labels for pattern recognition
/// - Rules: If-then relationships
/// - Policies: Action recommendations
///
/// This is the bridge from perception to understanding.
/// </summary>
public sealed class KnowledgeExtractor
{
    // Active learning patterns
    private readonly Dictionary<string, LearnedPattern> _patterns = new();

    // Observation buffer for pattern discovery
    private readonly List<ObservationFrame> _observationBuffer = new();
    private readonly int _maxBufferSize;

    // Knowledge database
    private readonly List<ExtractedRule> _rules = new();
    private readonly List<ExtractedPolicy> _policies = new();

    // Statistics
    private int _totalExtractions;
    private int _patternsLearned;

    public int PatternCount => _patterns.Count;
    public int RuleCount => _rules.Count;
    public int PolicyCount => _policies.Count;

    public KnowledgeExtractor(int maxBufferSize = 1000)
    {
        _maxBufferSize = maxBufferSize;
        InitializeSeedPatterns();
    }

    /// <summary>
    /// Extract knowledge tags from current detections.
    /// </summary>
    public List<KnowledgeTag> Extract(
        IReadOnlyList<IntelligentDetection> detections,
        long frameId)
    {
        _totalExtractions++;
        var tags = new List<KnowledgeTag>();

        // Buffer this observation
        BufferObservation(detections, frameId);

        // Extract tags from each detection
        foreach (var detection in detections)
        {
            var detectionTags = ExtractDetectionTags(detection);
            tags.AddRange(detectionTags);

            // Apply tags to detection
            foreach (var tag in detectionTags)
            {
                detection.AddTag(tag);
            }
        }

        // Extract scene-level tags
        var sceneTags = ExtractSceneTags(detections);
        tags.AddRange(sceneTags);

        // Periodic pattern discovery
        if (frameId % 100 == 0)
        {
            DiscoverPatterns();
        }

        return tags;
    }

    /// <summary>
    /// Record outcome for pattern refinement.
    /// </summary>
    public void RecordOutcome(ActionOutcome outcome)
    {
        // Update patterns with outcome
        foreach (var pattern in _patterns.Values)
        {
            pattern.RecordOutcome(outcome);
        }

        // Update rules
        foreach (var rule in _rules)
        {
            rule.RecordOutcome(outcome);
        }

        // Prune unreliable patterns
        var toRemove = _patterns
            .Where(p => p.Value.Reliability < 0.2f && p.Value.ObservationCount > 50)
            .Select(p => p.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            _patterns.Remove(key);
        }
    }

    /// <summary>
    /// Extract tags from a single detection.
    /// </summary>
    private List<KnowledgeTag> ExtractDetectionTags(IntelligentDetection detection)
    {
        var tags = new List<KnowledgeTag>();

        // === BEHAVIOR TAGS ===
        if (detection.IsMoving)
        {
            tags.Add(new KnowledgeTag
            {
                Name = "moving",
                Category = "behavior",
                Confidence = Math.Min(1f, detection.Speed),
                Source = TagSource.Observation
            });

            if (detection.IsApproaching)
            {
                tags.Add(new KnowledgeTag
                {
                    Name = "approaching",
                    Category = "behavior",
                    Confidence = 0.8f,
                    Source = TagSource.Observation
                });
            }
        }
        else
        {
            tags.Add(new KnowledgeTag
            {
                Name = "stationary",
                Category = "behavior",
                Confidence = 1f - detection.Speed,
                Source = TagSource.Observation
            });
        }

        // === THREAT TAGS ===
        if (detection.IsThreat)
        {
            tags.Add(new KnowledgeTag
            {
                Name = "threat",
                Category = "threat",
                Confidence = detection.ThreatScore,
                Source = TagSource.Classification
            });

            if (detection.ThreatScore > 0.8f)
            {
                tags.Add(new KnowledgeTag
                {
                    Name = "high_priority_threat",
                    Category = "threat",
                    Confidence = detection.ThreatScore,
                    Source = TagSource.Classification
                });
            }
        }

        // === OPPORTUNITY TAGS ===
        if (detection.IsOpportunity)
        {
            tags.Add(new KnowledgeTag
            {
                Name = "opportunity",
                Category = "opportunity",
                Confidence = detection.OpportunityScore,
                Source = TagSource.Classification
            });

            // Specific opportunity type
            string oppType = detection.Class switch
            {
                DetectionClass.HealthKit => "health_pickup",
                DetectionClass.AmmoBox => "ammo_pickup",
                DetectionClass.WeaponPickup => "weapon_pickup",
                DetectionClass.Loot => "loot",
                _ => "resource"
            };

            tags.Add(new KnowledgeTag
            {
                Name = oppType,
                Category = "opportunity",
                Confidence = detection.OpportunityScore,
                Source = TagSource.Classification
            });
        }

        // === TRUST TAGS ===
        if (detection.TrustLevel >= TrustLevel.Certified)
        {
            tags.Add(new KnowledgeTag
            {
                Name = $"trust_{detection.TrustLevel.ToString().ToLower()}",
                Category = "trust",
                Confidence = detection.TrustScore,
                Source = TagSource.Authority
            });
        }

        // === LEARNED PATTERN TAGS ===
        foreach (var pattern in _patterns.Values)
        {
            if (pattern.Matches(detection))
            {
                tags.Add(new KnowledgeTag
                {
                    Name = pattern.TagName,
                    Category = "learned",
                    Confidence = pattern.Confidence,
                    Source = TagSource.Learning
                });
            }
        }

        return tags;
    }

    /// <summary>
    /// Extract scene-level tags.
    /// </summary>
    private List<KnowledgeTag> ExtractSceneTags(IReadOnlyList<IntelligentDetection> detections)
    {
        var tags = new List<KnowledgeTag>();

        int threatCount = detections.Count(d => d.IsThreat);
        int oppCount = detections.Count(d => d.IsOpportunity);

        // Scene threat level
        if (threatCount > 0)
        {
            string threatLevel = threatCount switch
            {
                1 => "single_threat",
                <= 3 => "multi_threat",
                _ => "swarm"
            };

            tags.Add(new KnowledgeTag
            {
                Name = threatLevel,
                Category = "scene",
                Confidence = Math.Min(1f, threatCount * 0.3f),
                Source = TagSource.SceneAnalysis
            });
        }

        // Scene opportunity
        if (oppCount > 0)
        {
            tags.Add(new KnowledgeTag
            {
                Name = oppCount > 2 ? "resource_cluster" : "resource_available",
                Category = "scene",
                Confidence = Math.Min(1f, oppCount * 0.3f),
                Source = TagSource.SceneAnalysis
            });
        }

        // Scene safety
        if (threatCount == 0)
        {
            tags.Add(new KnowledgeTag
            {
                Name = "safe_zone",
                Category = "scene",
                Confidence = 0.8f,
                Source = TagSource.SceneAnalysis
            });
        }

        // Approaching threats
        int approachingCount = detections.Count(d => d.IsThreat && d.IsApproaching);
        if (approachingCount > 0)
        {
            tags.Add(new KnowledgeTag
            {
                Name = "incoming_threat",
                Category = "scene",
                Confidence = Math.Min(1f, approachingCount * 0.4f),
                Source = TagSource.SceneAnalysis
            });
        }

        return tags;
    }

    /// <summary>
    /// Buffer observation for pattern learning.
    /// </summary>
    private void BufferObservation(IReadOnlyList<IntelligentDetection> detections, long frameId)
    {
        _observationBuffer.Add(new ObservationFrame
        {
            FrameId = frameId,
            DetectionCount = detections.Count,
            ThreatCount = detections.Count(d => d.IsThreat),
            OpportunityCount = detections.Count(d => d.IsOpportunity),
            AverageConfidence = detections.Count > 0 ? detections.Average(d => d.Confidence) : 0,
            DetectionFingerprints = detections.Select(d => GetDetectionFingerprint(d)).ToList()
        });

        // Trim buffer
        while (_observationBuffer.Count > _maxBufferSize)
        {
            _observationBuffer.RemoveAt(0);
        }
    }

    /// <summary>
    /// Discover patterns from observation buffer.
    /// </summary>
    private void DiscoverPatterns()
    {
        if (_observationBuffer.Count < 100) return;

        // Find recurring fingerprints
        var fingerprints = _observationBuffer
            .SelectMany(o => o.DetectionFingerprints)
            .GroupBy(f => f)
            .Where(g => g.Count() >= 10)
            .OrderByDescending(g => g.Count())
            .Take(20);

        foreach (var group in fingerprints)
        {
            string fingerprint = group.Key;
            if (_patterns.ContainsKey(fingerprint)) continue;

            // Create new pattern
            var pattern = LearnedPattern.FromFingerprint(fingerprint, group.Count());
            _patterns[fingerprint] = pattern;
            _patternsLearned++;
        }
    }

    /// <summary>
    /// Get fingerprint for a detection.
    /// </summary>
    private static string GetDetectionFingerprint(IntelligentDetection detection)
    {
        string motion = detection.Speed switch
        {
            < 0.3f => "static",
            < 1f => "slow",
            < 2f => "medium",
            _ => "fast"
        };

        string size = detection.Area switch
        {
            < 1000 => "small",
            < 5000 => "medium",
            _ => "large"
        };

        string type = detection.IsThreat ? "threat" : detection.IsOpportunity ? "opp" : "neutral";

        return $"{motion}_{size}_{type}";
    }

    /// <summary>
    /// Initialize seed patterns (known good patterns).
    /// </summary>
    private void InitializeSeedPatterns()
    {
        // Fast approaching threat
        _patterns["fast_approaching_threat"] = new LearnedPattern
        {
            Fingerprint = "fast_*_threat",
            TagName = "fast_threat",
            RequiresMoving = true,
            RequiresApproaching = true,
            MinSpeed = 1.5f,
            IsThreat = true,
            Confidence = 0.9f,
            ObservationCount = 100
        };

        // Stationary item
        _patterns["static_item"] = new LearnedPattern
        {
            Fingerprint = "static_*_opp",
            TagName = "pickup_item",
            RequiresMoving = false,
            MaxSpeed = 0.1f,
            IsOpportunity = true,
            Confidence = 0.85f,
            ObservationCount = 100
        };

        // Kiting target (slow but persistent)
        _patterns["slow_persistent_threat"] = new LearnedPattern
        {
            Fingerprint = "slow_*_threat",
            TagName = "kite_target",
            RequiresMoving = true,
            MinSpeed = 0.3f,
            MaxSpeed = 1f,
            IsThreat = true,
            Confidence = 0.7f,
            ObservationCount = 50
        };
    }

    /// <summary>
    /// Get all active rules.
    /// </summary>
    public IEnumerable<ExtractedRule> GetRules() => _rules;

    /// <summary>
    /// Get all active policies.
    /// </summary>
    public IEnumerable<ExtractedPolicy> GetPolicies() => _policies;
}

/// <summary>
/// A knowledge tag extracted from observation.
/// </summary>
public sealed class KnowledgeTag
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public float Confidence { get; init; }
    public TagSource Source { get; init; }
    public long ExtractedFrame { get; init; }

    public override string ToString() => $"{Category}:{Name} ({Confidence:P0})";
}

/// <summary>
/// Source of a knowledge tag.
/// </summary>
public enum TagSource
{
    Observation,
    Classification,
    Authority,
    Learning,
    SceneAnalysis
}

/// <summary>
/// Observation frame for pattern learning.
/// </summary>
internal sealed class ObservationFrame
{
    public long FrameId { get; init; }
    public int DetectionCount { get; init; }
    public int ThreatCount { get; init; }
    public int OpportunityCount { get; init; }
    public float AverageConfidence { get; init; }
    public List<string> DetectionFingerprints { get; init; } = new();
}

/// <summary>
/// Learned pattern from observations.
/// </summary>
internal sealed class LearnedPattern
{
    public string Fingerprint { get; init; } = "";
    public string TagName { get; init; } = "";
    public bool RequiresMoving { get; init; }
    public bool? RequiresApproaching { get; init; }
    public float MinSpeed { get; init; }
    public float MaxSpeed { get; init; } = float.MaxValue;
    public bool IsThreat { get; init; }
    public bool IsOpportunity { get; init; }
    public float Confidence { get; set; }
    public int ObservationCount { get; set; }
    public float Reliability { get; private set; } = 0.5f;

    private int _successCount;
    private int _totalOutcomes;

    public bool Matches(IntelligentDetection detection)
    {
        if (RequiresMoving && !detection.IsMoving) return false;
        if (!RequiresMoving && detection.IsMoving) return false;
        if (RequiresApproaching.HasValue && detection.IsApproaching != RequiresApproaching) return false;
        if (detection.Speed < MinSpeed || detection.Speed > MaxSpeed) return false;
        if (IsThreat && !detection.IsThreat) return false;
        if (IsOpportunity && !detection.IsOpportunity) return false;

        return true;
    }

    public void RecordOutcome(ActionOutcome outcome)
    {
        _totalOutcomes++;
        if (outcome.Success > 0)
            _successCount++;

        if (_totalOutcomes > 5)
        {
            Reliability = (float)_successCount / _totalOutcomes;
            Confidence = Confidence * 0.95f + Reliability * 0.05f;
        }
    }

    public static LearnedPattern FromFingerprint(string fingerprint, int count)
    {
        var parts = fingerprint.Split('_');
        bool moving = parts[0] != "static";
        bool threat = parts.Contains("threat");
        bool opp = parts.Contains("opp");

        return new LearnedPattern
        {
            Fingerprint = fingerprint,
            TagName = $"learned_{fingerprint}",
            RequiresMoving = moving,
            IsThreat = threat,
            IsOpportunity = opp,
            Confidence = Math.Min(0.9f, count * 0.01f),
            ObservationCount = count
        };
    }
}

/// <summary>
/// Extracted rule from pattern analysis.
/// </summary>
public sealed class ExtractedRule
{
    public string Id { get; init; } = "";
    public string Description { get; init; } = "";
    public string Antecedent { get; init; } = "";
    public string Consequent { get; init; } = "";
    public float Confidence { get; set; }
    public int Confirmations { get; set; }
    public int Violations { get; set; }

    public void RecordOutcome(ActionOutcome outcome)
    {
        if (outcome.Success > 0)
            Confirmations++;
        else
            Violations++;

        if (Confirmations + Violations > 5)
        {
            Confidence = (float)Confirmations / (Confirmations + Violations);
        }
    }
}

/// <summary>
/// Extracted policy from behavior analysis.
/// </summary>
public sealed class ExtractedPolicy
{
    public string Id { get; init; } = "";
    public string Description { get; init; } = "";
    public string Condition { get; init; } = "";
    public ActionType RecommendedAction { get; init; }
    public float Confidence { get; set; }
    public int Uses { get; set; }
    public int Successes { get; set; }
}
