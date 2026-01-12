using System.Text.Json;

namespace OKET.Agent.Learning.Knowledge;

/// <summary>
/// Self-organizing knowledge system that discovers, validates, and promotes knowledge.
/// Implements the "Law of Potential" hierarchy:
///   Laws → Rules → Policies → Conditions → Covenants → Principles → Traditions
/// </summary>
public sealed class KnowledgeOrganizer
{
    private readonly Dictionary<string, KnowledgeUnit> _knowledge = new();
    private readonly Dictionary<KnowledgeLevel, HashSet<string>> _byLevel = new();
    private readonly Dictionary<string, HashSet<string>> _byTag = new();
    private readonly PatternDetector _detector;

    // Configuration
    private readonly float _promotionThreshold;
    private readonly float _demotionThreshold;
    private readonly int _discoveryInterval;
    private readonly string _persistencePath;

    // Statistics
    private int _totalObservations;
    private int _lastDiscoveryAt;
    private int _promotions;
    private int _demotions;
    private int _discoveries;

    public int KnowledgeCount => _knowledge.Count;
    public int TotalObservations => _totalObservations;

    public KnowledgeOrganizer(
        float promotionThreshold = 0.9f,
        float demotionThreshold = 0.5f,
        int discoveryInterval = 1000,
        string persistencePath = "knowledge/knowledge_base.json")
    {
        _promotionThreshold = promotionThreshold;
        _demotionThreshold = demotionThreshold;
        _discoveryInterval = discoveryInterval;
        _persistencePath = persistencePath;
        _detector = new PatternDetector();

        // Initialize level indices
        foreach (KnowledgeLevel level in Enum.GetValues<KnowledgeLevel>())
        {
            _byLevel[level] = new HashSet<string>();
        }

        // Seed with fundamental laws
        SeedFundamentalKnowledge();
    }

    /// <summary>
    /// Observe a state transition and update knowledge accordingly.
    /// </summary>
    public void Observe(
        float[] state,
        int action,
        float reward,
        float[] nextState,
        bool terminal,
        Dictionary<string, float>? context = null)
    {
        _totalObservations++;

        // Record transition for pattern detection
        _detector.RecordTransition(state, action, reward, nextState, terminal, context);

        // Test existing knowledge against this observation
        TestKnowledge(state, action, reward, nextState, context);

        // Periodically discover new patterns
        if (_totalObservations - _lastDiscoveryAt >= _discoveryInterval)
        {
            DiscoverAndOrganize();
            _lastDiscoveryAt = _totalObservations;
        }
    }

    /// <summary>
    /// Query knowledge relevant to current state.
    /// </summary>
    public List<KnowledgeUnit> QueryRelevant(float[] state, Dictionary<string, float>? context = null)
    {
        return _knowledge.Values
            .Where(k => k.Antecedent.Matches(state, context))
            .OrderByDescending(k => k.Level)
            .ThenByDescending(k => k.Confidence)
            .ToList();
    }

    /// <summary>
    /// Query knowledge by level.
    /// </summary>
    public List<KnowledgeUnit> QueryByLevel(KnowledgeLevel level)
    {
        return _byLevel[level]
            .Select(id => _knowledge[id])
            .OrderByDescending(k => k.Confidence)
            .ToList();
    }

    /// <summary>
    /// Query knowledge by tag.
    /// </summary>
    public List<KnowledgeUnit> QueryByTag(string tag)
    {
        if (!_byTag.TryGetValue(tag, out var ids)) return new();
        return ids.Select(id => _knowledge[id]).ToList();
    }

    /// <summary>
    /// Get suggested action based on current knowledge.
    /// Higher-level knowledge takes precedence.
    /// </summary>
    public (int action, float confidence, string reason)? GetSuggestedAction(
        float[] state,
        Dictionary<string, float>? context = null)
    {
        var relevant = QueryRelevant(state, context);

        // Check in order of hierarchy (Laws first, then Rules, etc.)
        foreach (var level in Enum.GetValues<KnowledgeLevel>().OrderBy(l => l))
        {
            var levelKnowledge = relevant.Where(k => k.Level == level && k.IsStable).ToList();

            foreach (var knowledge in levelKnowledge)
            {
                // Look for policy/action suggestions
                if (knowledge.Tags.Contains("policy") || knowledge.Tags.Contains("action"))
                {
                    var actionCondition = knowledge.Consequent.Conditions
                        .FirstOrDefault(c => c.Feature == "suggested_action");

                    if (actionCondition != null)
                    {
                        return ((int)actionCondition.Threshold, knowledge.Confidence, knowledge.Description);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Get action modifiers based on applicable covenants and principles.
    /// </summary>
    public Dictionary<string, float> GetActionModifiers(float[] state, Dictionary<string, float>? context = null)
    {
        var modifiers = new Dictionary<string, float>();
        var relevant = QueryRelevant(state, context);

        // Apply covenants (constraints)
        foreach (var covenant in relevant.Where(k => k.Level == KnowledgeLevel.Covenant))
        {
            foreach (var tag in covenant.Tags)
            {
                if (tag.StartsWith("avoid_"))
                {
                    modifiers[tag] = -covenant.Confidence;
                }
                else if (tag.StartsWith("prefer_"))
                {
                    modifiers[tag] = covenant.Confidence;
                }
            }
        }

        // Apply principles (heuristics)
        foreach (var principle in relevant.Where(k => k.Level == KnowledgeLevel.Principle))
        {
            if (principle.Tags.Contains("caution"))
            {
                modifiers["risk_aversion"] = (modifiers.GetValueOrDefault("risk_aversion") + principle.Confidence) / 2;
            }
            if (principle.Tags.Contains("aggression"))
            {
                modifiers["aggression"] = (modifiers.GetValueOrDefault("aggression") + principle.Confidence) / 2;
            }
        }

        return modifiers;
    }

    /// <summary>
    /// Discover new patterns and reorganize knowledge.
    /// </summary>
    public void DiscoverAndOrganize()
    {
        // Discover new patterns
        var newPatterns = _detector.DiscoverPatterns();

        foreach (var pattern in newPatterns)
        {
            if (!_knowledge.ContainsKey(pattern.Id))
            {
                AddKnowledge(pattern);
                _discoveries++;
            }
        }

        // Reorganize existing knowledge
        ReorganizeKnowledge();

        // Prune unreliable knowledge
        PruneKnowledge();
    }

    /// <summary>
    /// Test all knowledge against an observation.
    /// </summary>
    private void TestKnowledge(
        float[] state,
        int action,
        float reward,
        float[] nextState,
        Dictionary<string, float>? context)
    {
        foreach (var knowledge in _knowledge.Values)
        {
            // Check if antecedent matches current state
            if (!knowledge.Antecedent.Matches(state, context))
                continue;

            // Check if consequent matches outcome
            bool consequentMatches = knowledge.Consequent.Matches(nextState, context);

            // For reward-based patterns, check reward direction
            if (knowledge.Tags.Contains("reward"))
            {
                bool expectsPositive = knowledge.Consequent.Expression.Contains("positive");
                consequentMatches = (expectsPositive && reward > 0) || (!expectsPositive && reward <= 0);
            }

            // For action patterns, check if suggested action matches
            if (knowledge.Tags.Contains("policy"))
            {
                var suggestedAction = knowledge.Consequent.Conditions
                    .FirstOrDefault(c => c.Feature == "suggested_action");
                if (suggestedAction != null)
                {
                    bool actionMatched = (int)suggestedAction.Threshold == action;
                    // Only count as confirmation if action was taken and reward was positive
                    if (actionMatched && reward > 0)
                        consequentMatches = true;
                    else if (actionMatched && reward <= 0)
                        consequentMatches = false;
                    else
                        continue; // Different action taken, don't update
                }
            }

            if (consequentMatches)
                knowledge.RecordConfirmation();
            else
                knowledge.RecordViolation();
        }
    }

    /// <summary>
    /// Reorganize knowledge by promoting/demoting based on reliability.
    /// </summary>
    private void ReorganizeKnowledge()
    {
        var toPromote = new List<KnowledgeUnit>();
        var toDemote = new List<KnowledgeUnit>();

        foreach (var knowledge in _knowledge.Values)
        {
            if (knowledge.ShouldPromote && knowledge.Level > KnowledgeLevel.Law)
            {
                toPromote.Add(knowledge);
            }
            else if (knowledge.ShouldDemote && knowledge.Level < KnowledgeLevel.Tradition)
            {
                toDemote.Add(knowledge);
            }
        }

        // Process promotions
        foreach (var knowledge in toPromote)
        {
            PromoteKnowledge(knowledge);
        }

        // Process demotions
        foreach (var knowledge in toDemote)
        {
            DemoteKnowledge(knowledge);
        }
    }

    private void PromoteKnowledge(KnowledgeUnit knowledge)
    {
        var oldLevel = knowledge.Level;
        var newLevel = (KnowledgeLevel)Math.Max(0, (int)oldLevel - 1);

        _byLevel[oldLevel].Remove(knowledge.Id);
        _byLevel[newLevel].Add(knowledge.Id);

        // Create promoted version
        var promoted = new KnowledgeUnit
        {
            Id = knowledge.Id,
            Level = newLevel,
            Description = knowledge.Description + $" [promoted from {oldLevel}]",
            Antecedent = knowledge.Antecedent,
            Consequent = knowledge.Consequent,
            Confidence = knowledge.Confidence,
            Confirmations = knowledge.Confirmations,
            Violations = knowledge.Violations,
            DiscoveredAt = knowledge.DiscoveredAt,
            LastConfirmedAt = knowledge.LastConfirmedAt,
            LastViolatedAt = knowledge.LastViolatedAt,
            Tags = knowledge.Tags,
            RelatedIds = knowledge.RelatedIds
        };

        _knowledge[knowledge.Id] = promoted;
        _promotions++;
    }

    private void DemoteKnowledge(KnowledgeUnit knowledge)
    {
        var oldLevel = knowledge.Level;
        var newLevel = (KnowledgeLevel)Math.Min(6, (int)oldLevel + 1);

        _byLevel[oldLevel].Remove(knowledge.Id);
        _byLevel[newLevel].Add(knowledge.Id);

        // Create demoted version
        var demoted = new KnowledgeUnit
        {
            Id = knowledge.Id,
            Level = newLevel,
            Description = knowledge.Description + $" [demoted from {oldLevel}]",
            Antecedent = knowledge.Antecedent,
            Consequent = knowledge.Consequent,
            Confidence = knowledge.Confidence * 0.9f, // Reduce confidence on demotion
            Confirmations = knowledge.Confirmations,
            Violations = knowledge.Violations,
            DiscoveredAt = knowledge.DiscoveredAt,
            LastConfirmedAt = knowledge.LastConfirmedAt,
            LastViolatedAt = knowledge.LastViolatedAt,
            Tags = knowledge.Tags,
            RelatedIds = knowledge.RelatedIds
        };

        _knowledge[knowledge.Id] = demoted;
        _demotions++;
    }

    private void PruneKnowledge()
    {
        var toRemove = _knowledge.Values
            .Where(k => k.Level == KnowledgeLevel.Tradition
                       && k.Reliability < 0.3f
                       && k.Violations > 20)
            .Select(k => k.Id)
            .ToList();

        foreach (var id in toRemove)
        {
            RemoveKnowledge(id);
        }
    }

    private void AddKnowledge(KnowledgeUnit knowledge)
    {
        _knowledge[knowledge.Id] = knowledge;
        _byLevel[knowledge.Level].Add(knowledge.Id);

        foreach (var tag in knowledge.Tags)
        {
            if (!_byTag.ContainsKey(tag))
                _byTag[tag] = new HashSet<string>();
            _byTag[tag].Add(knowledge.Id);
        }
    }

    private void RemoveKnowledge(string id)
    {
        if (!_knowledge.TryGetValue(id, out var knowledge))
            return;

        _knowledge.Remove(id);
        _byLevel[knowledge.Level].Remove(id);

        foreach (var tag in knowledge.Tags)
        {
            _byTag[tag]?.Remove(id);
        }
    }

    /// <summary>
    /// Seed with fundamental knowledge that should always hold.
    /// </summary>
    private void SeedFundamentalKnowledge()
    {
        // Law: Health loss means damage
        AddKnowledge(new KnowledgeUnit
        {
            Id = "law_damage",
            Level = KnowledgeLevel.Law,
            Description = "Taking damage reduces health",
            Antecedent = new KnowledgePattern
            {
                Expression = "any",
                Conditions = new()
            },
            Consequent = new KnowledgePattern
            {
                Expression = "health_decreased → damage_taken",
                Conditions = new()
            },
            Confidence = 0.99f,
            Confirmations = 1000,
            Tags = new() { "health", "damage", "fundamental" }
        });

        // Law: Death occurs at zero health
        AddKnowledge(new KnowledgeUnit
        {
            Id = "law_death",
            Level = KnowledgeLevel.Law,
            Description = "Zero health means death",
            Antecedent = new KnowledgePattern
            {
                Expression = "health = 0",
                Conditions = new List<PatternCondition>
                {
                    new() { Feature = "health", Operator = ComparisonOp.LessOrEqual, Threshold = 0, FeatureIndex = FeatureIndices.Health }
                }
            },
            Consequent = new KnowledgePattern
            {
                Expression = "terminal",
                Conditions = new()
            },
            Confidence = 0.99f,
            Confirmations = 1000,
            Tags = new() { "health", "death", "fundamental" }
        });

        // Rule: Shooting requires ammo
        AddKnowledge(new KnowledgeUnit
        {
            Id = "rule_ammo",
            Level = KnowledgeLevel.Rule,
            Description = "Cannot shoot without ammunition",
            Antecedent = new KnowledgePattern
            {
                Expression = "ammo_clip = 0",
                Conditions = new List<PatternCondition>
                {
                    new() { Feature = "ammo_clip", Operator = ComparisonOp.LessOrEqual, Threshold = 0, FeatureIndex = FeatureIndices.AmmoClip }
                }
            },
            Consequent = new KnowledgePattern
            {
                Expression = "cannot_shoot",
                Conditions = new()
            },
            Confidence = 0.95f,
            Confirmations = 500,
            Tags = new() { "ammo", "combat", "fundamental" }
        });

        // Covenant: Preserve health
        AddKnowledge(new KnowledgeUnit
        {
            Id = "covenant_preserve_health",
            Level = KnowledgeLevel.Covenant,
            Description = "Prioritize health preservation over aggression",
            Antecedent = new KnowledgePattern
            {
                Expression = "health < 0.3",
                Conditions = new List<PatternCondition>
                {
                    new() { Feature = "health", Operator = ComparisonOp.LessThan, Threshold = 0.3f, FeatureIndex = FeatureIndices.Health }
                }
            },
            Consequent = new KnowledgePattern
            {
                Expression = "prefer_defensive",
                Conditions = new()
            },
            Confidence = 0.85f,
            Tags = new() { "health", "survival", "prefer_defensive", "avoid_aggression" }
        });

        // Principle: Moving targets are harder
        AddKnowledge(new KnowledgeUnit
        {
            Id = "principle_movement",
            Level = KnowledgeLevel.Principle,
            Description = "Movement makes you harder to hit",
            Antecedent = new KnowledgePattern
            {
                Expression = "in_combat",
                Conditions = new List<PatternCondition>
                {
                    new() { Feature = "threats_in_fov", Operator = ComparisonOp.GreaterThan, Threshold = 0, FeatureIndex = FeatureIndices.ThreatsInFov }
                }
            },
            Consequent = new KnowledgePattern
            {
                Expression = "movement_beneficial",
                Conditions = new()
            },
            Confidence = 0.75f,
            Tags = new() { "movement", "combat", "caution" }
        });
    }

    /// <summary>
    /// Save knowledge base to disk.
    /// </summary>
    public void Save()
    {
        var dir = Path.GetDirectoryName(_persistencePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var data = new KnowledgePersistence
        {
            Knowledge = _knowledge.Values.ToList(),
            TotalObservations = _totalObservations,
            Promotions = _promotions,
            Demotions = _demotions,
            Discoveries = _discoveries,
            SavedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_persistencePath, json);
    }

    /// <summary>
    /// Load knowledge base from disk.
    /// </summary>
    public bool Load()
    {
        if (!File.Exists(_persistencePath))
            return false;

        try
        {
            var json = File.ReadAllText(_persistencePath);
            var data = JsonSerializer.Deserialize<KnowledgePersistence>(json);
            if (data == null) return false;

            _knowledge.Clear();
            foreach (var level in _byLevel.Values) level.Clear();
            _byTag.Clear();

            foreach (var unit in data.Knowledge)
            {
                AddKnowledge(unit);
            }

            _totalObservations = data.TotalObservations;
            _promotions = data.Promotions;
            _demotions = data.Demotions;
            _discoveries = data.Discoveries;

            return true;
        }
        catch
        {
            return false;
        }
    }

    public string GetDiagnostics()
    {
        var levelCounts = _byLevel.ToDictionary(
            kv => kv.Key.ToString(),
            kv => kv.Value.Count);

        return $"""
            === KNOWLEDGE ORGANIZER ===
            Total Knowledge: {_knowledge.Count}
            Observations: {_totalObservations:N0}
            Discoveries: {_discoveries}
            Promotions: {_promotions}
            Demotions: {_demotions}

            By Level:
              Laws: {levelCounts.GetValueOrDefault("Law")}
              Rules: {levelCounts.GetValueOrDefault("Rule")}
              Policies: {levelCounts.GetValueOrDefault("Policy")}
              Conditions: {levelCounts.GetValueOrDefault("Condition")}
              Covenants: {levelCounts.GetValueOrDefault("Covenant")}
              Principles: {levelCounts.GetValueOrDefault("Principle")}
              Traditions: {levelCounts.GetValueOrDefault("Tradition")}

            Pattern Detector: {_detector.HistorySize} records, {_detector.CandidateCount} candidates
            ===========================
            """;
    }
}

/// <summary>
/// Persistence format for knowledge base.
/// </summary>
public sealed class KnowledgePersistence
{
    public List<KnowledgeUnit> Knowledge { get; init; } = new();
    public int TotalObservations { get; init; }
    public int Promotions { get; init; }
    public int Demotions { get; init; }
    public int Discoveries { get; init; }
    public DateTime SavedAt { get; init; }
}
