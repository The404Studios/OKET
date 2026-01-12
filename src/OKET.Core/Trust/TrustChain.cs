namespace OKET.Core.Trust;

/// <summary>
/// Trust Chain - Hierarchical chain of trust from root to instance.
///
/// ARCHITECTURE:
///
///   ROOT TRUST (Hardcoded survival axioms)
///        │
///        ├── DOMAIN: Threat
///        │     ├── PATTERN: Zombie behavior
///        │     │     └── INSTANCE: This specific zombie
///        │     └── PATTERN: FastZombie behavior
///        │
///        ├── DOMAIN: Resource
///        │     ├── PATTERN: HealthKit behavior
///        │     └── PATTERN: AmmoCrate behavior
///        │
///        └── DOMAIN: Navigation
///              └── PATTERN: Safe corridor
///
/// Trust flows DOWN the chain:
/// - Root trust is absolute (score = 1.0)
/// - Each level inherits trust from parent
/// - Trust degrades with each level
/// - Override strength = trust * reliability * level_factor
///
/// Verification flows UP the chain:
/// - Instance verified → Pattern verified → Domain verified → Root verified
/// - Any break in chain invalidates descendants
/// </summary>
public sealed class TrustChain
{
    private readonly CertificateAuthority _rootCA;
    private readonly Dictionary<string, TrustPath> _activePaths = new();
    private readonly List<ChainValidation> _validationHistory = new();

    // Chain metrics
    private float _overallTrust;
    private int _chainDepth;
    private int _totalNodes;
    private int _validNodes;

    public CertificateAuthority RootCA => _rootCA;
    public float OverallTrust => _overallTrust;
    public int ChainDepth => _chainDepth;
    public int TotalNodes => _totalNodes;
    public int ValidNodes => _validNodes;

    public TrustChain()
    {
        _rootCA = new CertificateAuthority("ROOT", CertificateLevel.Root);
        _rootCA.InitializeDomainCAs();
        ComputeChainMetrics();
    }

    /// <summary>
    /// Verify the entire chain for a certificate.
    /// </summary>
    public ChainVerificationResult VerifyChain(TrustCertificate cert, CertificateContext context)
    {
        var chain = cert.GetChain();
        var verifications = new List<(TrustCertificate cert, ValidationResult result)>();

        float chainTrust = 1.0f;
        bool isValid = true;
        string? breakPoint = null;

        // Verify from leaf to root
        foreach (var chainCert in chain)
        {
            var result = chainCert.Validate(context);
            verifications.Add((chainCert, result));

            if (!result.IsValid)
            {
                isValid = false;
                breakPoint = chainCert.CertificateId;
                break;
            }

            // Trust degrades along chain
            chainTrust *= chainCert.TrustScore * GetLevelDegradation(chainCert.Level);
        }

        // Record validation
        _validationHistory.Add(new ChainValidation
        {
            CertificateId = cert.CertificateId,
            Timestamp = DateTime.UtcNow,
            ChainLength = chain.Count,
            IsValid = isValid,
            FinalTrust = chainTrust
        });

        return new ChainVerificationResult
        {
            IsValid = isValid,
            ChainTrust = chainTrust,
            ChainLength = chain.Count,
            BreakPoint = breakPoint,
            Verifications = verifications,
            CanOverride = isValid && chainTrust > 0.5f
        };
    }

    /// <summary>
    /// Get the trust path for a subject.
    /// </summary>
    public TrustPath? GetTrustPath(string subject)
    {
        return _activePaths.GetValueOrDefault(subject);
    }

    /// <summary>
    /// Register a new trust path.
    /// </summary>
    public void RegisterPath(string subject, TrustCertificate cert)
    {
        var chain = cert.GetChain();

        _activePaths[subject] = new TrustPath
        {
            Subject = subject,
            LeafCert = cert,
            Chain = chain,
            Depth = chain.Count,
            ComputedTrust = ComputePathTrust(chain)
        };

        ComputeChainMetrics();
    }

    /// <summary>
    /// Invalidate a trust path (when cert revoked or expired).
    /// </summary>
    public void InvalidatePath(string subject)
    {
        _activePaths.Remove(subject);
        ComputeChainMetrics();
    }

    /// <summary>
    /// Get the most trusted certificate for a target type.
    /// </summary>
    public TrustCertificate? GetMostTrusted(string targetType, CertificateContext context)
    {
        TrustCertificate? best = null;
        float bestTrust = 0;

        // Check root CA
        var rootBest = _rootCA.GetBestCertificate(context, targetType);
        if (rootBest != null)
        {
            var verification = VerifyChain(rootBest, context);
            if (verification.IsValid && verification.ChainTrust > bestTrust)
            {
                best = rootBest;
                bestTrust = verification.ChainTrust;
            }
        }

        return best;
    }

    /// <summary>
    /// Submit pattern for certification through the chain.
    /// </summary>
    public string SubmitForCertification(
        string domain,
        string subject,
        CertifiedBehavior behavior,
        int prototypeId,
        float reliability)
    {
        var domainCA = _rootCA.GetDomainCA(domain);
        if (domainCA == null)
        {
            // Use root CA if no domain CA
            return _rootCA.SubmitForCertification(subject, behavior, prototypeId, reliability);
        }

        return domainCA.SubmitForCertification(subject, behavior, prototypeId, reliability);
    }

    /// <summary>
    /// Record outcome for pending certification.
    /// </summary>
    public CertificationProgress RecordOutcome(string domain, string pendingId, bool success)
    {
        var domainCA = _rootCA.GetDomainCA(domain);
        if (domainCA != null)
        {
            return domainCA.RecordPendingOutcome(pendingId, success);
        }

        return _rootCA.RecordPendingOutcome(pendingId, success);
    }

    private float ComputePathTrust(List<TrustCertificate> chain)
    {
        float trust = 1.0f;
        foreach (var cert in chain)
        {
            trust *= cert.TrustScore * GetLevelDegradation(cert.Level);
        }
        return trust;
    }

    private static float GetLevelDegradation(CertificateLevel level)
    {
        return level switch
        {
            CertificateLevel.Root => 1.0f,
            CertificateLevel.Domain => 0.95f,
            CertificateLevel.Pattern => 0.9f,
            CertificateLevel.Instance => 0.85f,
            _ => 0.8f
        };
    }

    private void ComputeChainMetrics()
    {
        _totalNodes = 0;
        _validNodes = 0;
        _chainDepth = 0;
        float trustSum = 0;

        foreach (var path in _activePaths.Values)
        {
            _totalNodes += path.Chain.Count;
            _validNodes += path.Chain.Count(c => c.IsValid);
            _chainDepth = Math.Max(_chainDepth, path.Depth);
            trustSum += path.ComputedTrust;
        }

        _overallTrust = _activePaths.Count > 0
            ? trustSum / _activePaths.Count
            : 1.0f;
    }

    /// <summary>
    /// Get diagnostics.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"""
            === TRUST CHAIN ===
            Overall Trust: {_overallTrust:F2}
            Chain Depth: {_chainDepth}
            Nodes: {_validNodes}/{_totalNodes} valid
            Active Paths: {_activePaths.Count}

            Recent Validations:
            {string.Join("\n", _validationHistory
                .TakeLast(5)
                .Select(v => $"  {v.CertificateId}: {(v.IsValid ? "VALID" : "INVALID")} trust={v.FinalTrust:F2}"))}

            {_rootCA.GetDiagnostics()}
            ===================
            """;
    }
}

/// <summary>
/// A path through the trust chain.
/// </summary>
public sealed class TrustPath
{
    public string Subject { get; init; } = "";
    public TrustCertificate LeafCert { get; init; } = null!;
    public List<TrustCertificate> Chain { get; init; } = new();
    public int Depth { get; init; }
    public float ComputedTrust { get; init; }
}

/// <summary>
/// Result of chain verification.
/// </summary>
public readonly struct ChainVerificationResult
{
    public bool IsValid { get; init; }
    public float ChainTrust { get; init; }
    public int ChainLength { get; init; }
    public string? BreakPoint { get; init; }
    public List<(TrustCertificate cert, ValidationResult result)> Verifications { get; init; }
    public bool CanOverride { get; init; }
}

/// <summary>
/// Record of a chain validation.
/// </summary>
internal readonly struct ChainValidation
{
    public string CertificateId { get; init; }
    public DateTime Timestamp { get; init; }
    public int ChainLength { get; init; }
    public bool IsValid { get; init; }
    public float FinalTrust { get; init; }
}
