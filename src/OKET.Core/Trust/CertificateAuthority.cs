namespace OKET.Core.Trust;

/// <summary>
/// Certificate Authority - Issues, validates, and manages trust certificates.
///
/// ARCHITECTURE:
///
///   [ROOT CA] ← Hardcoded survival behaviors (absolute trust)
///       ↓
///   [DOMAIN CAs] ← Category authorities (Threat, Resource, Navigation)
///       ↓
///   [PATTERN CERTS] ← Learned patterns that proved reliable
///       ↓
///   [INSTANCE CERTS] ← Individual object certifications
///
/// Certification process:
/// 1. Pattern emerges from gradient/prototype system
/// 2. Pattern proves reliable over N encounters
/// 3. CA evaluates certification criteria
/// 4. If passed, certificate is issued
/// 5. Certificate grants override authority
///
/// The CA maintains:
/// - Certificate revocation list (CRL)
/// - Trust store (all valid certs)
/// - Pending certifications (not yet proven)
/// </summary>
public sealed class CertificateAuthority
{
    private readonly string _authorityId;
    private readonly CertificateLevel _authorityLevel;

    // Root certificate (self-signed for root CA)
    private TrustCertificate _rootCert;

    // All issued certificates
    private readonly Dictionary<string, TrustCertificate> _issuedCerts = new();

    // Revocation list
    private readonly HashSet<string> _revokedCerts = new();

    // Pending certifications (being evaluated)
    private readonly Dictionary<string, PendingCertification> _pending = new();

    // Domain sub-authorities
    private readonly Dictionary<string, CertificateAuthority> _domainCAs = new();

    // Certification criteria
    private readonly CertificationCriteria _criteria;

    // Statistics
    private int _totalIssued;
    private int _totalRevoked;
    private int _totalValidations;

    public string AuthorityId => _authorityId;
    public CertificateLevel Level => _authorityLevel;
    public TrustCertificate RootCert => _rootCert;
    public int IssuedCount => _issuedCerts.Count;
    public int RevokedCount => _revokedCerts.Count;
    public int PendingCount => _pending.Count;

    public CertificateAuthority(
        string authorityId,
        CertificateLevel level,
        CertificationCriteria? criteria = null)
    {
        _authorityId = authorityId;
        _authorityLevel = level;
        _criteria = criteria ?? CertificationCriteria.Default;

        // Create root certificate
        if (level == CertificateLevel.Root)
        {
            _rootCert = CreateRootCertificate();
        }
    }

    /// <summary>
    /// Create the root certificate with hardcoded survival behaviors.
    /// </summary>
    private TrustCertificate CreateRootCertificate()
    {
        var behavior = new CertifiedBehavior
        {
            Action = CertifiedAction.Trust,
            TargetType = "*",
            ExpectedOutcome = 1.0f,
            Priority = 100
        };

        var constraints = new CertificateConstraints();

        var rootCert = new TrustCertificate(
            $"ROOT_{_authorityId}",
            CertificateLevel.Root,
            "Root Trust",
            _authorityId,
            behavior,
            constraints,
            TimeSpan.FromDays(365 * 100) // Very long validity
        );

        _issuedCerts[rootCert.CertificateId] = rootCert;
        return rootCert;
    }

    /// <summary>
    /// Initialize domain certificate authorities.
    /// </summary>
    public void InitializeDomainCAs()
    {
        if (_authorityLevel != CertificateLevel.Root)
            return;

        // Create domain CAs
        var domains = new[] { "Threat", "Resource", "Navigation", "Combat", "Survival" };

        foreach (var domain in domains)
        {
            var domainCA = new CertificateAuthority(
                $"{_authorityId}_{domain}",
                CertificateLevel.Domain,
                CertificationCriteria.ForDomain(domain));

            // Issue domain certificate from root
            var domainCert = IssueCertificate(
                CertificateLevel.Domain,
                domain,
                new CertifiedBehavior
                {
                    Action = CertifiedAction.Trust,
                    TargetType = domain,
                    ExpectedOutcome = 0.8f,
                    Priority = 50
                },
                new CertificateConstraints(),
                TimeSpan.FromDays(30));

            domainCA._rootCert = domainCert;
            _domainCAs[domain] = domainCA;
        }

        // Add hardcoded root behaviors
        AddRootBehaviors();
    }

    /// <summary>
    /// Add hardcoded root-level behaviors that are always trusted.
    /// </summary>
    private void AddRootBehaviors()
    {
        // Always flee when health critical
        IssueCertificate(
            CertificateLevel.Root,
            "CriticalHealthFlee",
            new CertifiedBehavior
            {
                Action = CertifiedAction.Flee,
                TargetType = "Threat",
                ExpectedOutcome = 0.9f,
                Priority = 100
            },
            new CertificateConstraints
            {
                MaxThreat = 1.0f, // Any threat
                CustomPredicate = ctx => ctx.Health < 0.15f
            },
            TimeSpan.FromDays(365 * 100));

        // Always collect health when low
        IssueCertificate(
            CertificateLevel.Root,
            "LowHealthCollect",
            new CertifiedBehavior
            {
                Action = CertifiedAction.Collect,
                TargetType = "HealthKit",
                ExpectedOutcome = 0.95f,
                Priority = 90
            },
            new CertificateConstraints
            {
                CustomPredicate = ctx => ctx.Health < 0.5f
            },
            TimeSpan.FromDays(365 * 100));

        // Always engage single close threat with ammo
        IssueCertificate(
            CertificateLevel.Root,
            "SingleThreatEngage",
            new CertifiedBehavior
            {
                Action = CertifiedAction.Engage,
                TargetType = "Threat",
                ExpectedOutcome = 0.7f,
                Priority = 70
            },
            new CertificateConstraints
            {
                MinHealth = 0.3f,
                MaxThreat = 0.6f // Single non-overwhelming threat
            },
            TimeSpan.FromDays(365 * 100));
    }

    /// <summary>
    /// Submit a pattern for certification consideration.
    /// </summary>
    public string SubmitForCertification(
        string subject,
        CertifiedBehavior proposedBehavior,
        int prototypeId,
        float currentReliability)
    {
        string pendingId = $"PENDING_{_authorityId}_{subject}_{DateTime.UtcNow.Ticks}";

        var pending = new PendingCertification
        {
            PendingId = pendingId,
            Subject = subject,
            ProposedBehavior = proposedBehavior,
            PrototypeId = prototypeId,
            InitialReliability = currentReliability,
            SubmittedAt = DateTime.UtcNow,
            SuccessCount = 0,
            FailureCount = 0,
            ValidationCount = 0
        };

        _pending[pendingId] = pending;
        return pendingId;
    }

    /// <summary>
    /// Record outcome for a pending certification.
    /// </summary>
    public CertificationProgress RecordPendingOutcome(string pendingId, bool success)
    {
        if (!_pending.TryGetValue(pendingId, out var pending))
            return new CertificationProgress { Status = CertificationStatus.NotFound };

        pending.ValidationCount++;
        if (success)
            pending.SuccessCount++;
        else
            pending.FailureCount++;

        // Check if certification criteria met
        if (MeetsCriteria(pending))
        {
            // Graduate to full certificate
            var cert = IssueCertificate(
                CertificateLevel.Pattern,
                pending.Subject,
                pending.ProposedBehavior,
                new CertificateConstraints(),
                TimeSpan.FromDays(7));

            _pending.Remove(pendingId);

            return new CertificationProgress
            {
                Status = CertificationStatus.Certified,
                CertificateId = cert.CertificateId,
                Progress = 1.0f
            };
        }

        // Check if should be rejected
        if (ShouldReject(pending))
        {
            _pending.Remove(pendingId);
            return new CertificationProgress
            {
                Status = CertificationStatus.Rejected,
                Reason = "Failed to meet reliability criteria"
            };
        }

        // Still pending
        float progress = (float)pending.ValidationCount / _criteria.MinValidations;
        return new CertificationProgress
        {
            Status = CertificationStatus.Pending,
            Progress = Math.Min(progress, 0.99f),
            SuccessRate = pending.ValidationCount > 0
                ? (float)pending.SuccessCount / pending.ValidationCount
                : 0
        };
    }

    /// <summary>
    /// Issue a new certificate.
    /// </summary>
    public TrustCertificate IssueCertificate(
        CertificateLevel level,
        string subject,
        CertifiedBehavior behavior,
        CertificateConstraints constraints,
        TimeSpan validity)
    {
        string certId = $"CERT_{_authorityId}_{level}_{subject}_{DateTime.UtcNow.Ticks}";

        var cert = new TrustCertificate(
            certId,
            level,
            subject,
            _authorityId,
            behavior,
            constraints,
            validity);

        // Link to issuer
        if (level != CertificateLevel.Root && _rootCert != null)
        {
            cert.SetIssuer(_rootCert);
        }

        _issuedCerts[certId] = cert;
        _totalIssued++;

        return cert;
    }

    /// <summary>
    /// Validate a certificate.
    /// </summary>
    public ValidationResult ValidateCertificate(string certId, CertificateContext context)
    {
        _totalValidations++;

        if (_revokedCerts.Contains(certId))
            return new ValidationResult(false, "Certificate is revoked");

        if (!_issuedCerts.TryGetValue(certId, out var cert))
            return new ValidationResult(false, "Certificate not found");

        return cert.Validate(context);
    }

    /// <summary>
    /// Get the best applicable certificate for a situation.
    /// </summary>
    public TrustCertificate? GetBestCertificate(CertificateContext context, string targetType)
    {
        TrustCertificate? best = null;
        float bestStrength = 0;

        foreach (var cert in _issuedCerts.Values)
        {
            // Check if cert applies to this target type
            if (cert.Behavior.TargetType != "*" && cert.Behavior.TargetType != targetType)
                continue;

            var validation = cert.Validate(context);
            if (validation.IsValid && validation.OverrideStrength > bestStrength)
            {
                best = cert;
                bestStrength = validation.OverrideStrength;
            }
        }

        // Also check domain CAs
        foreach (var domainCA in _domainCAs.Values)
        {
            var domainBest = domainCA.GetBestCertificate(context, targetType);
            if (domainBest != null)
            {
                var validation = domainBest.Validate(context);
                if (validation.OverrideStrength > bestStrength)
                {
                    best = domainBest;
                    bestStrength = validation.OverrideStrength;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Revoke a certificate.
    /// </summary>
    public void RevokeCertificate(string certId, string reason)
    {
        if (_issuedCerts.TryGetValue(certId, out var cert))
        {
            cert.Revoke(reason);
            _revokedCerts.Add(certId);
            _totalRevoked++;
        }
    }

    /// <summary>
    /// Get a domain certificate authority.
    /// </summary>
    public CertificateAuthority? GetDomainCA(string domain)
    {
        return _domainCAs.GetValueOrDefault(domain);
    }

    private bool MeetsCriteria(PendingCertification pending)
    {
        if (pending.ValidationCount < _criteria.MinValidations)
            return false;

        float reliability = (float)pending.SuccessCount / pending.ValidationCount;
        if (reliability < _criteria.MinReliability)
            return false;

        var age = DateTime.UtcNow - pending.SubmittedAt;
        if (age < _criteria.MinAge)
            return false;

        return true;
    }

    private bool ShouldReject(PendingCertification pending)
    {
        if (pending.ValidationCount < 10)
            return false;

        float reliability = (float)pending.SuccessCount / pending.ValidationCount;
        if (reliability < 0.3f)
            return true;

        var age = DateTime.UtcNow - pending.SubmittedAt;
        if (age > _criteria.MaxAge && reliability < _criteria.MinReliability)
            return true;

        return false;
    }

    /// <summary>
    /// Get diagnostics.
    /// </summary>
    public string GetDiagnostics()
    {
        var validCerts = _issuedCerts.Values.Count(c => c.IsValid);
        var overrideCerts = _issuedCerts.Values.Count(c => c.CanOverride);

        return $"""
            === CERTIFICATE AUTHORITY: {_authorityId} ===
            Level: {_authorityLevel}
            Issued: {_totalIssued} (valid={validCerts}, override={overrideCerts})
            Revoked: {_totalRevoked}
            Pending: {_pending.Count}
            Validations: {_totalValidations}
            Domain CAs: {_domainCAs.Count}

            Top Certificates:
            {string.Join("\n", _issuedCerts.Values
                .Where(c => c.IsValid)
                .OrderByDescending(c => c.OverrideStrength)
                .Take(5)
                .Select(c => $"  {c}"))}
            =============================================
            """;
    }
}

/// <summary>
/// Certification criteria for issuing certificates.
/// </summary>
public sealed class CertificationCriteria
{
    public int MinValidations { get; init; } = 20;
    public float MinReliability { get; init; } = 0.7f;
    public TimeSpan MinAge { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromHours(1);

    public static CertificationCriteria Default => new();

    public static CertificationCriteria ForDomain(string domain)
    {
        return domain switch
        {
            "Threat" => new CertificationCriteria
            {
                MinValidations = 15,
                MinReliability = 0.75f,
                MinAge = TimeSpan.FromMinutes(3)
            },
            "Resource" => new CertificationCriteria
            {
                MinValidations = 10,
                MinReliability = 0.6f,
                MinAge = TimeSpan.FromMinutes(2)
            },
            "Combat" => new CertificationCriteria
            {
                MinValidations = 25,
                MinReliability = 0.8f,
                MinAge = TimeSpan.FromMinutes(5)
            },
            _ => Default
        };
    }
}

/// <summary>
/// Pending certification being evaluated.
/// </summary>
internal sealed class PendingCertification
{
    public string PendingId { get; init; } = "";
    public string Subject { get; init; } = "";
    public CertifiedBehavior ProposedBehavior { get; init; } = new();
    public int PrototypeId { get; init; }
    public float InitialReliability { get; init; }
    public DateTime SubmittedAt { get; init; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public int ValidationCount { get; set; }
}

/// <summary>
/// Progress of a pending certification.
/// </summary>
public readonly struct CertificationProgress
{
    public CertificationStatus Status { get; init; }
    public string? CertificateId { get; init; }
    public float Progress { get; init; }
    public float SuccessRate { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Status of certification process.
/// </summary>
public enum CertificationStatus
{
    NotFound,
    Pending,
    Certified,
    Rejected
}
