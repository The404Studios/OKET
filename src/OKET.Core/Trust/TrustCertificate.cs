namespace OKET.Core.Trust;

/// <summary>
/// Trust Certificate - Represents a certified pattern/behavior.
///
/// PRINCIPLE: Nothing is trusted until proven. Once proven, it becomes certified.
/// Certificates are OVERRIDES - they have authority over uncertified decisions.
///
/// Certification requires:
/// 1. Consistent positive outcomes over time
/// 2. Stability (low variance in behavior)
/// 3. Sufficient sample size
/// 4. Signed by a valid issuer (chain of trust)
///
/// Certificate hierarchy (like PKI):
/// - ROOT: Core survival behaviors (hardcoded, absolute trust)
/// - DOMAIN: Category-level patterns (threats, resources, navigation)
/// - PATTERN: Specific learned patterns (zombie behavior, item locations)
/// - INSTANCE: Individual object certifications (this specific zombie)
///
/// Certified patterns can OVERRIDE lower-trust decisions.
/// </summary>
public sealed class TrustCertificate
{
    private readonly string _certificateId;
    private readonly CertificateLevel _level;
    private readonly string _subject; // What this cert is about
    private readonly string _issuerId; // Who issued this cert
    private readonly DateTime _issuedAt;
    private DateTime _expiresAt;
    private DateTime _lastValidated;

    // Trust metrics
    private float _trustScore; // [0, 1] - how much to trust this
    private float _reliability; // [0, 1] - how consistent
    private int _validationCount; // How many times validated
    private int _successCount; // How many successful uses
    private int _failureCount; // How many failures
    private bool _isRevoked;
    private string? _revocationReason;

    // What this certificate authorizes
    private readonly CertifiedBehavior _behavior;
    private readonly CertificateConstraints _constraints;

    // Chain info
    private TrustCertificate? _issuerCert;
    private readonly List<TrustCertificate> _issuedCerts = new();

    public string CertificateId => _certificateId;
    public CertificateLevel Level => _level;
    public string Subject => _subject;
    public string IssuerId => _issuerId;
    public DateTime IssuedAt => _issuedAt;
    public DateTime ExpiresAt => _expiresAt;
    public float TrustScore => _trustScore;
    public float Reliability => _reliability;
    public int ValidationCount => _validationCount;
    public bool IsRevoked => _isRevoked;
    public string? RevocationReason => _revocationReason;
    public CertifiedBehavior Behavior => _behavior;
    public CertificateConstraints Constraints => _constraints;
    public TrustCertificate? IssuerCert => _issuerCert;
    public IReadOnlyList<TrustCertificate> IssuedCerts => _issuedCerts;

    /// <summary>Is this certificate currently valid?</summary>
    public bool IsValid => !_isRevoked &&
                          DateTime.UtcNow < _expiresAt &&
                          _trustScore > 0.3f &&
                          (_issuerCert?.IsValid ?? _level == CertificateLevel.Root);

    /// <summary>Can this certificate override decisions?</summary>
    public bool CanOverride => IsValid && _trustScore > 0.7f && _reliability > 0.6f;

    /// <summary>Override strength [0, 1].</summary>
    public float OverrideStrength => IsValid
        ? _trustScore * _reliability * GetLevelMultiplier()
        : 0f;

    public TrustCertificate(
        string certificateId,
        CertificateLevel level,
        string subject,
        string issuerId,
        CertifiedBehavior behavior,
        CertificateConstraints constraints,
        TimeSpan validity)
    {
        _certificateId = certificateId;
        _level = level;
        _subject = subject;
        _issuerId = issuerId;
        _behavior = behavior;
        _constraints = constraints;
        _issuedAt = DateTime.UtcNow;
        _expiresAt = _issuedAt + validity;
        _lastValidated = _issuedAt;
        _trustScore = GetInitialTrustScore(level);
        _reliability = 0.5f;
    }

    /// <summary>
    /// Validate this certificate (called when used).
    /// </summary>
    public ValidationResult Validate(CertificateContext context)
    {
        _validationCount++;
        _lastValidated = DateTime.UtcNow;

        // Check basic validity
        if (_isRevoked)
            return new ValidationResult(false, "Certificate revoked: " + _revocationReason);

        if (DateTime.UtcNow >= _expiresAt)
            return new ValidationResult(false, "Certificate expired");

        // Check chain
        if (_level != CertificateLevel.Root && _issuerCert != null)
        {
            var issuerValid = _issuerCert.Validate(context);
            if (!issuerValid.IsValid)
                return new ValidationResult(false, "Issuer invalid: " + issuerValid.Reason);
        }

        // Check constraints
        if (!_constraints.IsSatisfied(context))
            return new ValidationResult(false, "Constraints not satisfied");

        return new ValidationResult(true, "Valid", OverrideStrength);
    }

    /// <summary>
    /// Record outcome of using this certificate.
    /// </summary>
    public void RecordOutcome(bool success, float magnitude = 1f)
    {
        if (success)
        {
            _successCount++;
            _trustScore = Math.Min(1f, _trustScore + 0.02f * magnitude);
        }
        else
        {
            _failureCount++;
            _trustScore = Math.Max(0f, _trustScore - 0.05f * magnitude);

            // Revoke if too many failures
            if (_failureCount > _successCount * 2 && _validationCount > 10)
            {
                Revoke("Too many failures");
            }
        }

        // Update reliability
        float total = _successCount + _failureCount;
        if (total > 0)
        {
            _reliability = _successCount / total;
        }
    }

    /// <summary>
    /// Renew certificate validity.
    /// </summary>
    public void Renew(TimeSpan extension)
    {
        if (!_isRevoked && _trustScore > 0.5f)
        {
            _expiresAt = DateTime.UtcNow + extension;
        }
    }

    /// <summary>
    /// Revoke this certificate.
    /// </summary>
    public void Revoke(string reason)
    {
        _isRevoked = true;
        _revocationReason = reason;

        // Cascade revocation to issued certs
        foreach (var issued in _issuedCerts)
        {
            issued.Revoke("Issuer revoked: " + reason);
        }
    }

    /// <summary>
    /// Link to issuer certificate.
    /// </summary>
    public void SetIssuer(TrustCertificate issuer)
    {
        _issuerCert = issuer;
        issuer._issuedCerts.Add(this);
    }

    private float GetLevelMultiplier()
    {
        return _level switch
        {
            CertificateLevel.Root => 1.0f,
            CertificateLevel.Domain => 0.9f,
            CertificateLevel.Pattern => 0.7f,
            CertificateLevel.Instance => 0.5f,
            _ => 0.3f
        };
    }

    private static float GetInitialTrustScore(CertificateLevel level)
    {
        return level switch
        {
            CertificateLevel.Root => 1.0f,
            CertificateLevel.Domain => 0.7f,
            CertificateLevel.Pattern => 0.5f,
            CertificateLevel.Instance => 0.4f,
            _ => 0.3f
        };
    }

    /// <summary>
    /// Get the full chain from this cert to root.
    /// </summary>
    public List<TrustCertificate> GetChain()
    {
        var chain = new List<TrustCertificate> { this };
        var current = _issuerCert;
        while (current != null)
        {
            chain.Add(current);
            current = current._issuerCert;
        }
        return chain;
    }

    public override string ToString()
    {
        string status = _isRevoked ? "REVOKED" : (IsValid ? "VALID" : "INVALID");
        return $"Cert[{_certificateId}]: {_level}/{_subject} {status} " +
               $"trust={_trustScore:F2} rel={_reliability:F2} " +
               $"success={_successCount}/{_validationCount}";
    }
}

/// <summary>
/// Certificate hierarchy level.
/// </summary>
public enum CertificateLevel
{
    /// <summary>Root certificate - absolute trust, hardcoded behaviors.</summary>
    Root = 0,

    /// <summary>Domain certificate - category-level patterns.</summary>
    Domain = 1,

    /// <summary>Pattern certificate - specific learned patterns.</summary>
    Pattern = 2,

    /// <summary>Instance certificate - individual object certification.</summary>
    Instance = 3
}

/// <summary>
/// What behavior this certificate authorizes.
/// </summary>
public sealed class CertifiedBehavior
{
    /// <summary>The action this cert authorizes.</summary>
    public CertifiedAction Action { get; init; }

    /// <summary>Target type this applies to.</summary>
    public string TargetType { get; init; } = "*";

    /// <summary>Expected outcome when following this behavior.</summary>
    public float ExpectedOutcome { get; init; }

    /// <summary>Priority when multiple certs apply.</summary>
    public int Priority { get; init; }

    /// <summary>Additional parameters for the action.</summary>
    public Dictionary<string, float> Parameters { get; init; } = new();
}

/// <summary>
/// Certified action types.
/// </summary>
public enum CertifiedAction
{
    /// <summary>Trust this pattern (allow through gates).</summary>
    Trust,

    /// <summary>Engage this type of target.</summary>
    Engage,

    /// <summary>Flee from this type of target.</summary>
    Flee,

    /// <summary>Collect/interact with this type.</summary>
    Collect,

    /// <summary>Ignore this pattern.</summary>
    Ignore,

    /// <summary>Block this pattern (override to prevent).</summary>
    Block,

    /// <summary>Modulate processing for this pattern.</summary>
    Modulate
}

/// <summary>
/// Constraints that must be satisfied for certificate to apply.
/// </summary>
public sealed class CertificateConstraints
{
    /// <summary>Minimum health required.</summary>
    public float? MinHealth { get; init; }

    /// <summary>Maximum threat level allowed.</summary>
    public float? MaxThreat { get; init; }

    /// <summary>Required situation types.</summary>
    public HashSet<string>? RequiredSituations { get; init; }

    /// <summary>Forbidden situation types.</summary>
    public HashSet<string>? ForbiddenSituations { get; init; }

    /// <summary>Time of day constraints (game time).</summary>
    public (float start, float end)? TimeRange { get; init; }

    /// <summary>Custom predicate.</summary>
    public Func<CertificateContext, bool>? CustomPredicate { get; init; }

    public bool IsSatisfied(CertificateContext context)
    {
        if (MinHealth.HasValue && context.Health < MinHealth.Value)
            return false;

        if (MaxThreat.HasValue && context.ThreatLevel > MaxThreat.Value)
            return false;

        if (RequiredSituations != null && !RequiredSituations.Contains(context.SituationType))
            return false;

        if (ForbiddenSituations != null && ForbiddenSituations.Contains(context.SituationType))
            return false;

        if (CustomPredicate != null && !CustomPredicate(context))
            return false;

        return true;
    }
}

/// <summary>
/// Context for certificate validation.
/// </summary>
public readonly struct CertificateContext
{
    public float Health { get; init; }
    public float ThreatLevel { get; init; }
    public string SituationType { get; init; }
    public float SystemStrain { get; init; }
    public float GateGain { get; init; }
    public int TargetPrototypeId { get; init; }
    public string? TargetName { get; init; }
}

/// <summary>
/// Result of certificate validation.
/// </summary>
public readonly struct ValidationResult
{
    public bool IsValid { get; init; }
    public string Reason { get; init; }
    public float OverrideStrength { get; init; }

    public ValidationResult(bool isValid, string reason, float overrideStrength = 0)
    {
        IsValid = isValid;
        Reason = reason;
        OverrideStrength = overrideStrength;
    }
}
