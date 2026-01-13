namespace OKET.Core.Trust.Pipelines;

/// <summary>
/// Knowledge Pipeline - Learning, Memory, and Retrieval.
///
/// Knowledge is EARNED through experience, not assumed.
///
/// RAW KNOWLEDGE:
///   Input: certified outcomes + pattern associations + context
///   Transform: consolidation into retrievable patterns
///   Output: KnowledgeClaims (associations, procedures, facts)
///
/// TRUSTED KNOWLEDGE:
///   Gates (CAs):
///     CA-K0 Empirical Basis: derived from certified outcomes?
///     CA-K1 Consistency: doesn't contradict existing trusted knowledge?
///     CA-K2 Generalization: does it generalize beyond specific case?
///     CA-K3 Retrieval Validity: when retrieved, does it help?
///     CA-K4 Decay Resistance: has it persisted across contexts?
///
///   Sinks:
///     - Superstition sink (correlation without causation)
///     - Overfitting sink (too specific to generalize)
///     - Obsolete sink (outdated, no longer applies)
///     - Contamination sink (from unreliable source)
///
///   Output: CertifiedKnowledge with authority + applicability
///
/// RULE: Knowledge becomes trusted only through repeated validated use.
/// New knowledge starts as PROBE, must earn ALLOW status.
/// </summary>
public sealed class KnowledgePipeline
{
    // Certification authorities
    private readonly EmpiricalBasisCA _caEmpirical = new();
    private readonly ConsistencyCA _caConsistency = new();
    private readonly GeneralizationCA _caGeneralization = new();
    private readonly RetrievalValidityCA _caRetrieval = new();
    private readonly DecayResistanceCA _caDecay = new();

    // Sinks
    private readonly SuperstitionSink _superstitionSink = new();
    private readonly OverfittingSink _overfittingSink = new();
    private readonly ObsoleteSink _obsoleteSink = new();
    private readonly ContaminationSink _contaminationSink = new();

    // Knowledge store
    private readonly Dictionary<int, KnowledgeEntry> _store = new();
    private readonly Dictionary<string, List<int>> _tagIndex = new();
    private int _nextId;

    // Retrieval tracking
    private readonly Queue<RetrievalRecord> _retrievals = new();
    private const int MaxRetrievals = 200;

    // State
    private GateState _gateState = GateState.Probe;
    private readonly GateThresholds _thresholds;

    // Statistics
    private int _totalClaims;
    private int _certified;
    private int _sunk;
    private int _retrievalsSuccessful;
    private int _retrievalsFailed;

    public int KnowledgeCount => _store.Count;
    public int CertifiedCount => _store.Count(kv => kv.Value.IsCertified);
    public float CertificationRate => _totalClaims > 0 ? (float)_certified / _totalClaims : 0;
    public float RetrievalSuccessRate => (_retrievalsSuccessful + _retrievalsFailed) > 0
        ? (float)_retrievalsSuccessful / (_retrievalsSuccessful + _retrievalsFailed)
        : 0.5f;

    public KnowledgePipeline()
    {
        _thresholds = new GateThresholds
        {
            AllowThreshold = 0.75f,  // Higher threshold for knowledge
            ProbeThreshold = 0.45f,
            MaxRisk = 0.70f,
            MinCoherence = 0.40f,
            Hysteresis = 0.08f,
            Weights = new DimensionalWeights
            {
                Coherence = 0.20f,
                Stability = 0.25f,  // High weight - knowledge should be stable
                ContextFit = 0.15f,
                Risk = 0.10f,
                Reversibility = 0.05f,
                OutcomeHistory = 0.20f,  // High weight - empirical validation
                Novelty = 0.05f
            }.Normalized()
        };
    }

    /// <summary>
    /// Submit a knowledge claim for certification.
    /// </summary>
    public KnowledgeResult Process(KnowledgeClaim claim)
    {
        _totalClaims++;

        // === STAGE 1: COMPUTE DIMENSIONAL SCORES ===
        var scores = ComputeScores(claim);

        // === STAGE 2: RUN CERTIFICATION AUTHORITIES ===
        var caResults = new List<CertificationResult>
        {
            _caEmpirical.Certify(claim, scores),
            _caConsistency.Certify(claim, _store.Values.Where(e => e.IsCertified).ToList(), scores),
            _caGeneralization.Certify(claim, scores),
            _caRetrieval.Certify(claim, GetRetrievalStats(claim), scores),
            _caDecay.Certify(claim, scores)
        };

        scores = AggregateCaResults(caResults, scores);

        // === STAGE 3: GATE DECISION ===
        var decision = CognitiveGate.Evaluate(scores, _thresholds, _gateState);
        _gateState = decision.State;

        // === STAGE 4: SINK OR STORE ===
        if (decision.ShouldSink)
        {
            _sunk++;
            RouteToSinks(claim, decision);

            return new KnowledgeResult
            {
                Claim = claim,
                GateDecision = decision,
                IsCertified = false,
                SunkTo = GetActiveSink(claim, decision)
            };
        }

        if (decision.ShouldProbe)
        {
            // Store as provisional knowledge
            var provisional = StoreProvisional(claim, scores);

            return new KnowledgeResult
            {
                Claim = claim,
                GateDecision = decision,
                IsCertified = false,
                ProvisionalId = provisional.Id,
                ProbeAction = new ProbeAction
                {
                    Type = ProbeType.Test,
                    Target = claim,
                    SafetyMargin = 0.7f,
                    Duration = TimeSpan.FromSeconds(5),
                    Reasoning = $"Probe knowledge: needs {(int)((0.75f - decision.CompositeScore) * 100)}% more validation"
                }
            };
        }

        // === STAGE 5: CERTIFY ===
        _certified++;
        var certified = Certify(claim, scores, caResults);

        return new KnowledgeResult
        {
            Claim = claim,
            GateDecision = decision,
            IsCertified = true,
            CertifiedKnowledge = certified
        };
    }

    /// <summary>
    /// Retrieve relevant knowledge for a query.
    /// </summary>
    public RetrievalResult Retrieve(RetrievalQuery query)
    {
        var candidates = new List<(KnowledgeEntry entry, float relevance)>();

        // Search by tags
        foreach (var tag in query.Tags)
        {
            if (_tagIndex.TryGetValue(tag, out var ids))
            {
                foreach (var id in ids)
                {
                    if (_store.TryGetValue(id, out var entry) && entry.IsCertified)
                    {
                        float relevance = ComputeRelevance(entry, query);
                        candidates.Add((entry, relevance));
                    }
                }
            }
        }

        // Search by context similarity
        foreach (var entry in _store.Values.Where(e => e.IsCertified))
        {
            float contextMatch = ComputeContextMatch(entry, query);
            if (contextMatch > 0.5f)
            {
                float relevance = ComputeRelevance(entry, query);
                if (!candidates.Any(c => c.entry.Id == entry.Id))
                    candidates.Add((entry, relevance));
            }
        }

        // Rank and filter
        var ranked = candidates
            .OrderByDescending(c => c.relevance * c.entry.AuthorityScore)
            .Take(query.MaxResults)
            .ToList();

        // Record retrieval
        foreach (var (entry, relevance) in ranked)
        {
            RecordRetrieval(entry.Id, relevance);
        }

        return new RetrievalResult
        {
            Query = query,
            Retrieved = ranked.Select(c => c.entry).ToList(),
            Relevances = ranked.Select(c => c.relevance).ToList(),
            TotalCandidates = candidates.Count
        };
    }

    /// <summary>
    /// Record outcome of using retrieved knowledge.
    /// </summary>
    public void RecordRetrievalOutcome(int knowledgeId, bool wasHelpful, float impact)
    {
        if (_store.TryGetValue(knowledgeId, out var entry))
        {
            entry.RecordRetrieval(wasHelpful, impact);

            if (wasHelpful)
                _retrievalsSuccessful++;
            else
                _retrievalsFailed++;

            // Update retrieval record
            var recent = _retrievals.LastOrDefault(r => r.KnowledgeId == knowledgeId);
            if (recent != null)
            {
                recent.WasHelpful = wasHelpful;
                recent.Impact = impact;
            }
        }
    }

    /// <summary>
    /// Consolidate provisional knowledge based on outcomes.
    /// </summary>
    public void Consolidate()
    {
        var toPromote = new List<int>();
        var toDemote = new List<int>();

        foreach (var (id, entry) in _store)
        {
            if (!entry.IsCertified)
            {
                // Check if provisional knowledge should be promoted
                if (entry.RetrievalCount >= 3 && entry.RetrievalSuccessRate >= 0.7f)
                {
                    toPromote.Add(id);
                }
                // Or demoted due to failure
                else if (entry.RetrievalCount >= 3 && entry.RetrievalSuccessRate < 0.3f)
                {
                    toDemote.Add(id);
                }
                // Or expired
                else if (entry.FramesSinceCreation > 600 && entry.RetrievalCount < 2)
                {
                    toDemote.Add(id);
                }
            }
        }

        foreach (var id in toPromote)
            PromoteToCertified(id);

        foreach (var id in toDemote)
        {
            if (_store.TryGetValue(id, out var entry))
            {
                var decision = new GateDecision { State = GateState.Deny };
                RouteToSinks(entry.OriginalClaim, decision);
            }
            _store.Remove(id);
        }
    }

    /// <summary>
    /// Decay knowledge over time.
    /// </summary>
    public void Decay()
    {
        var toRemove = new List<int>();

        foreach (var (id, entry) in _store)
        {
            entry.FramesSinceCreation++;
            entry.FramesSinceLastUse++;

            // Decay authority for unused knowledge
            if (entry.FramesSinceLastUse > 300) // ~10 seconds
            {
                entry.DecayAuthority(0.995f);
            }

            // Remove dead knowledge
            if (entry.AuthorityScore < 0.1f)
            {
                toRemove.Add(id);
            }
        }

        foreach (var id in toRemove)
        {
            if (_store.TryGetValue(id, out var entry))
            {
                RemoveFromIndex(entry);
            }
            _store.Remove(id);
        }

        // Cleanup retrieval records
        while (_retrievals.Count > MaxRetrievals)
            _retrievals.Dequeue();
    }

    /// <summary>
    /// Compute dimensional scores for knowledge claim.
    /// </summary>
    private DimensionalScores ComputeScores(KnowledgeClaim claim)
    {
        return new DimensionalScores
        {
            Coherence = ComputeCoherence(claim),
            Stability = claim.ObservationCount >= 5 ? 0.8f : claim.ObservationCount * 0.15f,
            ContextFit = claim.ContextSpecificity,
            Risk = ComputeKnowledgeRisk(claim),
            Reversibility = 0.8f, // Knowledge can be revised
            OutcomeHistory = claim.SuccessRate,
            Novelty = _store.Values.Any(e => e.Type == claim.Type && e.Content == claim.Content) ? 0.1f : 0.6f
        };
    }

    private float ComputeCoherence(KnowledgeClaim claim)
    {
        // Check if claim is internally consistent
        if (claim.Type == KnowledgeType.Procedure && string.IsNullOrEmpty(claim.Content))
            return 0.3f;

        if (claim.Confidence > 0.9f && claim.ObservationCount < 3)
            return 0.5f; // Overconfident

        return Math.Min(1f, 0.5f + claim.Confidence * 0.3f + claim.ObservationCount * 0.02f);
    }

    private float ComputeKnowledgeRisk(KnowledgeClaim claim)
    {
        // Procedural knowledge has higher risk (actions have consequences)
        if (claim.Type == KnowledgeType.Procedure)
            return 0.4f;

        // Causal claims can lead to wrong predictions
        if (claim.Type == KnowledgeType.Causal)
            return 0.3f;

        return 0.2f;
    }

    private static DimensionalScores AggregateCaResults(
        List<CertificationResult> results,
        DimensionalScores baseScores)
    {
        float passCount = results.Count(r => r.Passed);
        float avgScore = results.Where(r => r.Passed).Select(r => r.Score).DefaultIfEmpty(0).Average();

        var scores = baseScores;
        scores.Coherence = (scores.Coherence + avgScore) / 2f;
        scores.OutcomeHistory = Math.Max(scores.OutcomeHistory, passCount / results.Count);

        return scores;
    }

    private RetrievalStats GetRetrievalStats(KnowledgeClaim claim)
    {
        // Check if similar knowledge exists and how well it retrieved
        var similar = _store.Values
            .Where(e => e.Type == claim.Type && e.IsCertified)
            .ToList();

        if (similar.Count == 0)
            return new RetrievalStats();

        return new RetrievalStats
        {
            SimilarCount = similar.Count,
            AvgSuccessRate = similar.Average(e => e.RetrievalSuccessRate),
            TotalRetrievals = similar.Sum(e => e.RetrievalCount)
        };
    }

    private KnowledgeEntry StoreProvisional(KnowledgeClaim claim, DimensionalScores scores)
    {
        var entry = new KnowledgeEntry
        {
            Id = _nextId++,
            Type = claim.Type,
            Content = claim.Content,
            Tags = claim.Tags,
            Context = claim.Context,
            AuthorityScore = scores.Coherence * scores.Stability * 0.5f, // Lower authority for provisional
            Scores = scores,
            IsCertified = false,
            OriginalClaim = claim
        };

        _store[entry.Id] = entry;
        IndexEntry(entry);

        return entry;
    }

    private CertifiedKnowledge Certify(
        KnowledgeClaim claim,
        DimensionalScores scores,
        List<CertificationResult> caResults)
    {
        var entry = new KnowledgeEntry
        {
            Id = _nextId++,
            Type = claim.Type,
            Content = claim.Content,
            Tags = claim.Tags,
            Context = claim.Context,
            AuthorityScore = scores.Coherence * scores.Stability * scores.OutcomeHistory,
            Scores = scores,
            IsCertified = true,
            CertificationChain = caResults.Where(r => r.Passed).Select(r => r.Reason).ToList(),
            OriginalClaim = claim
        };

        _store[entry.Id] = entry;
        IndexEntry(entry);

        return new CertifiedKnowledge
        {
            Id = entry.Id,
            Type = claim.Type,
            Content = claim.Content,
            Tags = claim.Tags,
            AuthorityScore = entry.AuthorityScore,
            Scores = scores,
            CertificationChain = entry.CertificationChain,
            CertifiedAt = DateTime.UtcNow
        };
    }

    private void PromoteToCertified(int id)
    {
        if (_store.TryGetValue(id, out var entry))
        {
            entry.IsCertified = true;
            entry.AuthorityScore = Math.Min(1f, entry.AuthorityScore * 1.5f);
            entry.CertificationChain.Add("Promoted from provisional after successful use");
            _certified++;
        }
    }

    private void IndexEntry(KnowledgeEntry entry)
    {
        foreach (var tag in entry.Tags)
        {
            if (!_tagIndex.ContainsKey(tag))
                _tagIndex[tag] = new List<int>();
            _tagIndex[tag].Add(entry.Id);
        }
    }

    private void RemoveFromIndex(KnowledgeEntry entry)
    {
        foreach (var tag in entry.Tags)
        {
            if (_tagIndex.TryGetValue(tag, out var list))
                list.Remove(entry.Id);
        }
    }

    private static float ComputeRelevance(KnowledgeEntry entry, RetrievalQuery query)
    {
        float tagMatch = entry.Tags.Count(t => query.Tags.Contains(t)) /
                        (float)Math.Max(1, query.Tags.Count);

        float contextMatch = ComputeContextMatch(entry, query);

        return tagMatch * 0.5f + contextMatch * 0.5f;
    }

    private static float ComputeContextMatch(KnowledgeEntry entry, RetrievalQuery query)
    {
        if (entry.Context == null || query.Context == null)
            return 0.5f;

        // Simple context comparison
        float threatDiff = Math.Abs(entry.Context.ThreatLevel - query.Context.ThreatLevel);
        float healthDiff = Math.Abs(entry.Context.Health - query.Context.Health);

        return 1f - (threatDiff + healthDiff) / 2f;
    }

    private void RecordRetrieval(int knowledgeId, float relevance)
    {
        _retrievals.Enqueue(new RetrievalRecord
        {
            KnowledgeId = knowledgeId,
            Relevance = relevance,
            Timestamp = DateTime.UtcNow
        });

        if (_store.TryGetValue(knowledgeId, out var entry))
        {
            entry.FramesSinceLastUse = 0;
        }
    }

    private void RouteToSinks(KnowledgeClaim claim, GateDecision decision)
    {
        if (_superstitionSink.ShouldCapture(claim, decision))
            _superstitionSink.Capture(claim, decision);
        else if (_overfittingSink.ShouldCapture(claim, decision))
            _overfittingSink.Capture(claim, decision);
        else if (_obsoleteSink.ShouldCapture(claim, decision))
            _obsoleteSink.Capture(claim, decision);
        else if (_contaminationSink.ShouldCapture(claim, decision))
            _contaminationSink.Capture(claim, decision);
    }

    private string? GetActiveSink(KnowledgeClaim claim, GateDecision decision)
    {
        if (_superstitionSink.ShouldCapture(claim, decision)) return _superstitionSink.Name;
        if (_overfittingSink.ShouldCapture(claim, decision)) return _overfittingSink.Name;
        if (_obsoleteSink.ShouldCapture(claim, decision)) return _obsoleteSink.Name;
        if (_contaminationSink.ShouldCapture(claim, decision)) return _contaminationSink.Name;
        return null;
    }

    public string GetDiagnostics()
    {
        int certified = _store.Count(kv => kv.Value.IsCertified);
        int provisional = _store.Count - certified;

        return $"""
            === KNOWLEDGE PIPELINE ===
            Store: {_store.Count} (certified={certified}, provisional={provisional})
            Claims: {_totalClaims}
            Certified: {_certified} ({CertificationRate:P0})
            Sunk: {_sunk}
            Retrieval Success: {RetrievalSuccessRate:P0}
            Sinks: superstition={_superstitionSink.CapturedCount}, overfit={_overfittingSink.CapturedCount}, obsolete={_obsoleteSink.CapturedCount}, contamination={_contaminationSink.CapturedCount}
            ==========================
            """;
    }
}

// ============== TYPES ==============

/// <summary>
/// Knowledge claim (uncertified).
/// </summary>
public readonly struct KnowledgeClaim
{
    public KnowledgeType Type { get; init; }
    public string Content { get; init; }
    public List<string> Tags { get; init; }
    public KnowledgeContext? Context { get; init; }
    public float Confidence { get; init; }
    public int ObservationCount { get; init; }
    public float SuccessRate { get; init; }
    public float ContextSpecificity { get; init; }
    public KnowledgeSource Source { get; init; }
}

/// <summary>
/// Types of knowledge.
/// </summary>
public enum KnowledgeType
{
    Associative,  // A is associated with B
    Procedural,   // How to do X
    Causal,       // X causes Y
    Categorical,  // X is a type of Y
    Contextual    // X applies in context C
}

/// <summary>
/// Source of knowledge.
/// </summary>
public enum KnowledgeSource
{
    Experience,   // Direct experience
    Inference,    // Derived from other knowledge
    Told,         // External information
    Instinct      // Built-in
}

/// <summary>
/// Context for knowledge.
/// </summary>
public sealed class KnowledgeContext
{
    public float ThreatLevel { get; init; }
    public float Health { get; init; }
    public int ThreatCount { get; init; }
    public string? Situation { get; init; }
}

/// <summary>
/// Certified knowledge.
/// </summary>
public sealed class CertifiedKnowledge
{
    public int Id { get; init; }
    public KnowledgeType Type { get; init; }
    public string Content { get; init; } = "";
    public List<string> Tags { get; init; } = new();
    public float AuthorityScore { get; init; }
    public DimensionalScores Scores { get; init; }
    public List<string> CertificationChain { get; init; } = new();
    public DateTime CertifiedAt { get; init; }
}

/// <summary>
/// Internal knowledge entry.
/// </summary>
internal sealed class KnowledgeEntry
{
    public int Id { get; init; }
    public KnowledgeType Type { get; init; }
    public string Content { get; init; } = "";
    public List<string> Tags { get; init; } = new();
    public KnowledgeContext? Context { get; init; }
    public float AuthorityScore { get; set; }
    public DimensionalScores Scores { get; init; }
    public bool IsCertified { get; set; }
    public List<string> CertificationChain { get; init; } = new();
    public KnowledgeClaim OriginalClaim { get; init; }

    // Usage tracking
    public int RetrievalCount { get; private set; }
    public int RetrievalSuccesses { get; private set; }
    public float RetrievalSuccessRate => RetrievalCount > 0 ? (float)RetrievalSuccesses / RetrievalCount : 0.5f;
    public int FramesSinceCreation { get; set; }
    public int FramesSinceLastUse { get; set; }

    public void RecordRetrieval(bool wasHelpful, float impact)
    {
        RetrievalCount++;
        if (wasHelpful)
        {
            RetrievalSuccesses++;
            AuthorityScore = Math.Min(1f, AuthorityScore + impact * 0.05f);
        }
        else
        {
            AuthorityScore = Math.Max(0.1f, AuthorityScore - impact * 0.1f);
        }
        FramesSinceLastUse = 0;
    }

    public void DecayAuthority(float rate)
    {
        AuthorityScore *= rate;
    }
}

/// <summary>
/// Result of knowledge processing.
/// </summary>
public readonly struct KnowledgeResult
{
    public KnowledgeClaim Claim { get; init; }
    public GateDecision GateDecision { get; init; }
    public bool IsCertified { get; init; }
    public CertifiedKnowledge? CertifiedKnowledge { get; init; }
    public int? ProvisionalId { get; init; }
    public string? SunkTo { get; init; }
    public ProbeAction? ProbeAction { get; init; }
}

/// <summary>
/// Query for knowledge retrieval.
/// </summary>
public readonly struct RetrievalQuery
{
    public List<string> Tags { get; init; }
    public KnowledgeContext? Context { get; init; }
    public KnowledgeType? TypeFilter { get; init; }
    public int MaxResults { get; init; }
}

/// <summary>
/// Result of knowledge retrieval.
/// </summary>
public readonly struct RetrievalResult
{
    public RetrievalQuery Query { get; init; }
    public List<KnowledgeEntry> Retrieved { get; init; }
    public List<float> Relevances { get; init; }
    public int TotalCandidates { get; init; }
}

/// <summary>
/// Stats about retrieval for a type.
/// </summary>
internal readonly struct RetrievalStats
{
    public int SimilarCount { get; init; }
    public float AvgSuccessRate { get; init; }
    public int TotalRetrievals { get; init; }
}

/// <summary>
/// Record of a retrieval.
/// </summary>
internal sealed class RetrievalRecord
{
    public int KnowledgeId { get; init; }
    public float Relevance { get; init; }
    public DateTime Timestamp { get; init; }
    public bool WasHelpful { get; set; }
    public float Impact { get; set; }
}

// ============== CERTIFICATION AUTHORITIES ==============

/// <summary>
/// CA-K0: Empirical Basis - derived from certified outcomes?
/// </summary>
internal sealed class EmpiricalBasisCA
{
    public CertificationResult Certify(KnowledgeClaim claim, DimensionalScores scores)
    {
        // Need sufficient observations
        if (claim.ObservationCount < 3)
            return CertificationResult.Fail($"Insufficient observations ({claim.ObservationCount})", scores);

        // Source matters
        if (claim.Source == KnowledgeSource.Told && claim.ObservationCount < 5)
            return CertificationResult.Fail("External knowledge needs more validation", scores);

        // Success rate should be reasonable
        if (claim.SuccessRate < 0.4f)
            return CertificationResult.Fail($"Low success rate ({claim.SuccessRate:P0})", scores);

        var updated = scores;
        updated.OutcomeHistory = claim.SuccessRate;
        return CertificationResult.Pass(claim.SuccessRate, updated, $"Empirically grounded ({claim.ObservationCount} obs)");
    }
}

/// <summary>
/// CA-K1: Consistency - doesn't contradict existing trusted knowledge?
/// </summary>
internal sealed class ConsistencyCA
{
    public CertificationResult Certify(
        KnowledgeClaim claim,
        List<KnowledgeEntry> certified,
        DimensionalScores scores)
    {
        // Check for contradictions
        foreach (var existing in certified.Where(e => e.Type == claim.Type))
        {
            if (Contradicts(claim, existing))
            {
                // Can override if much stronger evidence
                if (claim.ObservationCount > existing.OriginalClaim.ObservationCount * 2 &&
                    claim.SuccessRate > existing.OriginalClaim.SuccessRate + 0.2f)
                {
                    return CertificationResult.Pass(0.6f, scores, "Overrides weaker knowledge");
                }

                return CertificationResult.Fail($"Contradicts existing knowledge (id={existing.Id})", scores);
            }
        }

        return CertificationResult.Pass(0.8f, scores, "Consistent with existing knowledge");
    }

    private static bool Contradicts(KnowledgeClaim claim, KnowledgeEntry existing)
    {
        // Same type, same context, different conclusion
        if (claim.Type == existing.Type &&
            claim.Tags.Intersect(existing.Tags).Any())
        {
            // Check if content is opposite
            // This is simplified - would need better semantic comparison
            return claim.Content.GetHashCode() != existing.Content.GetHashCode() &&
                   claim.Tags.SequenceEqual(existing.Tags);
        }
        return false;
    }
}

/// <summary>
/// CA-K2: Generalization - does it generalize beyond specific case?
/// </summary>
internal sealed class GeneralizationCA
{
    public CertificationResult Certify(KnowledgeClaim claim, DimensionalScores scores)
    {
        // Check context specificity
        if (claim.ContextSpecificity > 0.9f && claim.Type != KnowledgeType.Contextual)
            return CertificationResult.Fail("Too context-specific to generalize", scores);

        // Need multiple observations across contexts
        if (claim.ObservationCount < 5 && claim.ContextSpecificity > 0.7f)
            return CertificationResult.Fail("Need more varied observations", scores);

        float generalizability = 1f - claim.ContextSpecificity * 0.5f;
        return CertificationResult.Pass(generalizability, scores, $"Generalizable ({generalizability:F2})");
    }
}

/// <summary>
/// CA-K3: Retrieval Validity - when retrieved, does it help?
/// </summary>
internal sealed class RetrievalValidityCA
{
    public CertificationResult Certify(
        KnowledgeClaim claim,
        RetrievalStats stats,
        DimensionalScores scores)
    {
        // If similar knowledge exists, check its success rate
        if (stats.SimilarCount > 0 && stats.TotalRetrievals > 10)
        {
            if (stats.AvgSuccessRate < 0.4f)
            {
                return CertificationResult.Fail($"Similar knowledge has poor retrieval success ({stats.AvgSuccessRate:P0})", scores);
            }
        }

        return CertificationResult.Pass(0.7f, scores, "Retrieval validity check passed");
    }
}

/// <summary>
/// CA-K4: Decay Resistance - has it persisted across contexts?
/// </summary>
internal sealed class DecayResistanceCA
{
    public CertificationResult Certify(KnowledgeClaim claim, DimensionalScores scores)
    {
        // New knowledge can't have proven decay resistance yet
        // But we can check if it's based on temporally spread observations
        if (claim.ObservationCount >= 5)
        {
            return CertificationResult.Pass(0.8f, scores, "Sufficient observation spread");
        }

        // Pass with lower score for newer knowledge
        return CertificationResult.Pass(0.5f, scores, "Limited decay resistance data");
    }
}

// ============== SINKS ==============

/// <summary>
/// Superstition Sink - correlation without causation.
/// </summary>
internal sealed class SuperstitionSink : BaseSink
{
    public override string Name => "Superstition";

    public override bool ShouldCapture(object claim, GateDecision decision)
    {
        if (claim is not KnowledgeClaim knowledge) return false;

        // Causal claim with low observations and high confidence
        return knowledge.Type == KnowledgeType.Causal &&
               knowledge.ObservationCount < 5 &&
               knowledge.Confidence > 0.7f;
    }
}

/// <summary>
/// Overfitting Sink - too specific to generalize.
/// </summary>
internal sealed class OverfittingSink : BaseSink
{
    public override string Name => "Overfitting";

    public override bool ShouldCapture(object claim, GateDecision decision)
    {
        if (claim is not KnowledgeClaim knowledge) return false;

        // Very context-specific with few observations
        return knowledge.ContextSpecificity > 0.85f &&
               knowledge.ObservationCount < 10 &&
               knowledge.Type != KnowledgeType.Contextual;
    }
}

/// <summary>
/// Obsolete Sink - outdated, no longer applies.
/// </summary>
internal sealed class ObsoleteSink : BaseSink
{
    public override string Name => "Obsolete";

    public override bool ShouldCapture(object claim, GateDecision decision)
    {
        if (claim is not KnowledgeClaim knowledge) return false;

        // Low recent success rate despite many observations
        return knowledge.ObservationCount > 10 &&
               knowledge.SuccessRate < 0.3f;
    }
}

/// <summary>
/// Contamination Sink - from unreliable source.
/// </summary>
internal sealed class ContaminationSink : BaseSink
{
    public override string Name => "Contamination";

    public override bool ShouldCapture(object claim, GateDecision decision)
    {
        if (claim is not KnowledgeClaim knowledge) return false;

        // External source with no validation
        return knowledge.Source == KnowledgeSource.Told &&
               knowledge.ObservationCount < 3 &&
               knowledge.SuccessRate < 0.5f;
    }
}
