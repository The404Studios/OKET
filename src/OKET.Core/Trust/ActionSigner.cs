namespace OKET.Core.Trust;

/// <summary>
/// Action Signer - The Signing Authority.
///
/// An action is authorized ONLY if:
/// 1. The active superstate is valid
/// 2. The token chain is intact
/// 3. The action has succeeded before in similar chains
/// 4. Risk < threshold
/// 5. (Optional) Info-gain justification for probes
///
/// This is equivalent to:
/// "This action is SIGNED by a trusted authority."
///
/// UNSIGNED actions can still exist — but only as PROBES, never commits.
///
/// Action authorization formula:
/// Q(a) = E[Reward | a, p] - λ·E[Risk | a, p] + β·E[InfoGain | a, p]
///
/// Authorize commit if:
/// - Token authorized AND
/// - Q(a) >= τ_Q AND
/// - Risk estimate < hard cap
///
/// Otherwise → only allow probe actions (safe moves that increase information)
/// </summary>
public sealed class ActionSigner
{
    private readonly TokenAuthorizationChain _authChain;

    // Risk parameters
    private const float RiskLambda = 0.8f; // Risk penalty weight
    private const float InfoGainBeta = 0.3f; // Info gain bonus weight
    private const float MinQForCommit = 0.10f;
    private const float MaxRiskForCommit = 0.30f;
    private const float MinConfidenceForCommit = 0.75f;

    // Statistics
    private int _totalRequests;
    private int _commitsSigned;
    private int _probesSigned;
    private int _rejected;

    public TokenAuthorizationChain AuthChain => _authChain;
    public int TotalRequests => _totalRequests;
    public int CommitsSigned => _commitsSigned;
    public int ProbesSigned => _probesSigned;
    public float CommitRate => _totalRequests > 0 ? (float)_commitsSigned / _totalRequests : 0;

    public ActionSigner(TokenAuthorizationChain authChain)
    {
        _authChain = authChain;
    }

    /// <summary>
    /// Request action signing.
    /// </summary>
    public SigningResult RequestSigning(SigningRequest request, long frameId)
    {
        _totalRequests++;

        // === STEP 1: VALIDATE TOKEN AUTHORIZATION ===
        var tokenAuth = _authChain.Authorize(request.TokenInput, frameId);

        if (!tokenAuth.IsAuthorized)
        {
            _rejected++;
            return SigningResult.Rejected(
                $"Token not authorized: {tokenAuth.RejectReason}",
                SigningRejectionReason.TokenNotAuthorized);
        }

        // === STEP 2: GET EXPECTED OUTCOME ===
        ActionExpectation expectation;

        if (tokenAuth.MatchedPrototypeId.HasValue)
        {
            expectation = _authChain.Vault.GetExpectedOutcome(
                tokenAuth.MatchedPrototypeId.Value,
                request.ProposedAction);
        }
        else
        {
            // Novel token - uncertain expectations
            expectation = new ActionExpectation
            {
                ExpectedReward = 0,
                ExpectedRisk = 0.5f,
                ExpectedInfoGain = 0.8f, // High info gain for novel
                Confidence = 0.2f,
                Trials = 0
            };
        }

        // === STEP 3: COMPUTE Q VALUE ===
        float qValue = ComputeQValue(expectation, request);

        // === STEP 4: CHECK SIGNING CONDITIONS ===
        var signingDecision = EvaluateSigningConditions(
            tokenAuth,
            expectation,
            qValue,
            request);

        // === STEP 5: SIGN OR DOWNGRADE TO PROBE ===
        if (signingDecision.CanCommit)
        {
            _commitsSigned++;
            return SigningResult.SignedCommit(
                request.ProposedAction,
                qValue,
                expectation,
                tokenAuth.AuthorizationScore,
                tokenAuth.MatchedPrototypeId,
                GenerateSignature(request, tokenAuth, qValue));
        }

        if (signingDecision.CanProbe)
        {
            _probesSigned++;
            var probeAction = GetSafeProbeAction(request);
            return SigningResult.SignedProbe(
                probeAction,
                qValue,
                expectation,
                tokenAuth.AuthorizationScore,
                signingDecision.ProbeReason);
        }

        _rejected++;
        return SigningResult.Rejected(
            signingDecision.RejectReason,
            signingDecision.RejectReasonCode);
    }

    /// <summary>
    /// Compute Q value for an action.
    /// Q(a) = E[Reward] - λ·E[Risk] + β·E[InfoGain]
    /// </summary>
    private static float ComputeQValue(ActionExpectation expectation, SigningRequest request)
    {
        float reward = expectation.ExpectedReward;
        float risk = expectation.ExpectedRisk;
        float infoGain = expectation.ExpectedInfoGain;

        // Adjust by situation
        if (request.IsUrgent)
        {
            // In urgent situations, weight reward higher
            reward *= 1.2f;
        }

        if (request.Health < 0.3f)
        {
            // Low health - increase risk penalty
            risk *= 1.5f;
        }

        return reward - RiskLambda * risk + InfoGainBeta * infoGain;
    }

    /// <summary>
    /// Evaluate signing conditions.
    /// </summary>
    private static SigningDecision EvaluateSigningConditions(
        TokenAuthorizationResult tokenAuth,
        ActionExpectation expectation,
        float qValue,
        SigningRequest request)
    {
        // Check Q threshold
        if (qValue < MinQForCommit)
        {
            // Q too low - but might allow probe
            if (expectation.ExpectedInfoGain > 0.5f)
            {
                return new SigningDecision
                {
                    CanCommit = false,
                    CanProbe = true,
                    ProbeReason = $"Q={qValue:F2} < {MinQForCommit}, but high info gain"
                };
            }

            return new SigningDecision
            {
                CanCommit = false,
                CanProbe = false,
                RejectReason = $"Q value {qValue:F2} < {MinQForCommit}",
                RejectReasonCode = SigningRejectionReason.QValueTooLow
            };
        }

        // Check risk cap
        if (expectation.ExpectedRisk > MaxRiskForCommit)
        {
            // Risk too high - but probe might be safe
            if (request.ProposedAction == ActionId.Observe ||
                request.ProposedAction == ActionId.Probe)
            {
                return new SigningDecision
                {
                    CanCommit = false,
                    CanProbe = true,
                    ProbeReason = $"Risk {expectation.ExpectedRisk:F2} > {MaxRiskForCommit}, downgrading to probe"
                };
            }

            return new SigningDecision
            {
                CanCommit = false,
                CanProbe = true,
                ProbeReason = $"Risk too high for commit, probe allowed"
            };
        }

        // Check confidence
        if (tokenAuth.AuthorizationScore < MinConfidenceForCommit)
        {
            return new SigningDecision
            {
                CanCommit = false,
                CanProbe = true,
                ProbeReason = $"Confidence {tokenAuth.AuthorizationScore:F2} < {MinConfidenceForCommit}"
            };
        }

        // Check specific action constraints
        if (!IsActionAllowed(request.ProposedAction, expectation, request))
        {
            return new SigningDecision
            {
                CanCommit = false,
                CanProbe = true,
                ProbeReason = "Action constraints not met"
            };
        }

        // All conditions met - can commit
        return new SigningDecision
        {
            CanCommit = true,
            CanProbe = true
        };
    }

    /// <summary>
    /// Check action-specific constraints.
    /// </summary>
    private static bool IsActionAllowed(
        ActionId action,
        ActionExpectation expectation,
        SigningRequest request)
    {
        return action switch
        {
            // Engage requires decent success history
            ActionId.Engage => expectation.Trials >= 3 && expectation.Confidence > 0.5f,

            // Flee always allowed
            ActionId.Flee => true,

            // Kite requires some experience
            ActionId.Kite => expectation.Trials >= 2,

            // Approach requires low threat
            ActionId.Approach => request.ThreatLevel < 0.5f,

            // Interact requires very low threat
            ActionId.Interact => request.ThreatLevel < 0.2f,

            // Observe/Probe always allowed
            ActionId.Observe or ActionId.Probe => true,

            // Ignore requires confidence
            ActionId.Ignore => expectation.Confidence > 0.6f,

            _ => true
        };
    }

    /// <summary>
    /// Get safe probe action for uncertain situations.
    /// </summary>
    private static ActionId GetSafeProbeAction(SigningRequest request)
    {
        // Probe before commit rule:
        // - Small strafe for parallax
        // - Small camera pan
        // - Wait 100-200ms and recheck

        if (request.ThreatLevel > 0.5f)
        {
            // Under threat - kite probe (maintain distance while observing)
            return ActionId.Probe; // Will be interpreted as safe positioning
        }

        if (request.ProposedAction == ActionId.Interact ||
            request.ProposedAction == ActionId.Approach)
        {
            // Approach cautiously
            return ActionId.Observe;
        }

        return ActionId.Probe;
    }

    /// <summary>
    /// Generate signature for signed action.
    /// </summary>
    private static ActionSignature GenerateSignature(
        SigningRequest request,
        TokenAuthorizationResult tokenAuth,
        float qValue)
    {
        return new ActionSignature
        {
            SignatureId = Guid.NewGuid().ToString("N")[..8],
            TokenId = request.TokenInput.TokenId,
            Action = request.ProposedAction,
            QValue = qValue,
            AuthScore = tokenAuth.AuthorizationScore,
            PrototypeId = tokenAuth.MatchedPrototypeId,
            Timestamp = DateTime.UtcNow,
            ChainScores = tokenAuth.Scores
        };
    }

    /// <summary>
    /// Record outcome of a signed action.
    /// </summary>
    public void RecordOutcome(ActionSignature signature, ActionOutcome outcome)
    {
        // Forward to auth chain
        _authChain.RecordOutcome(
            signature.TokenId,
            signature.Action,
            outcome.Success,
            outcome.Reward,
            outcome.Risk);
    }

    /// <summary>
    /// Get diagnostics.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"""
            === ACTION SIGNER ===
            Requests: {_totalRequests}
            Commits: {_commitsSigned} ({CommitRate:P1})
            Probes: {_probesSigned}
            Rejected: {_rejected}

            {_authChain.GetDiagnostics()}
            =====================
            """;
    }
}

/// <summary>
/// Request for action signing.
/// </summary>
public readonly struct SigningRequest
{
    public TokenAuthorizationInput TokenInput { get; init; }
    public ActionId ProposedAction { get; init; }
    public float ThreatLevel { get; init; }
    public float Health { get; init; }
    public bool IsUrgent { get; init; }
}

/// <summary>
/// Result of signing request.
/// </summary>
public readonly struct SigningResult
{
    public bool IsSigned { get; init; }
    public bool IsCommit { get; init; }
    public ActionId SignedAction { get; init; }
    public float QValue { get; init; }
    public ActionExpectation Expectation { get; init; }
    public float AuthorizationScore { get; init; }
    public int? PrototypeId { get; init; }
    public ActionSignature? Signature { get; init; }
    public string? RejectReason { get; init; }
    public SigningRejectionReason RejectReasonCode { get; init; }
    public string? ProbeReason { get; init; }

    public static SigningResult SignedCommit(
        ActionId action,
        float qValue,
        ActionExpectation expectation,
        float authScore,
        int? protoId,
        ActionSignature signature) =>
        new()
        {
            IsSigned = true,
            IsCommit = true,
            SignedAction = action,
            QValue = qValue,
            Expectation = expectation,
            AuthorizationScore = authScore,
            PrototypeId = protoId,
            Signature = signature
        };

    public static SigningResult SignedProbe(
        ActionId action,
        float qValue,
        ActionExpectation expectation,
        float authScore,
        string probeReason) =>
        new()
        {
            IsSigned = true,
            IsCommit = false,
            SignedAction = action,
            QValue = qValue,
            Expectation = expectation,
            AuthorizationScore = authScore,
            ProbeReason = probeReason
        };

    public static SigningResult Rejected(string reason, SigningRejectionReason code) =>
        new()
        {
            IsSigned = false,
            RejectReason = reason,
            RejectReasonCode = code
        };
}

/// <summary>
/// Signing decision details.
/// </summary>
internal readonly struct SigningDecision
{
    public bool CanCommit { get; init; }
    public bool CanProbe { get; init; }
    public string? RejectReason { get; init; }
    public SigningRejectionReason RejectReasonCode { get; init; }
    public string? ProbeReason { get; init; }
}

/// <summary>
/// Rejection reason codes.
/// </summary>
public enum SigningRejectionReason
{
    None,
    TokenNotAuthorized,
    QValueTooLow,
    RiskTooHigh,
    ConfidenceTooLow,
    ActionConstraintsFailed
}

/// <summary>
/// Signature for a signed action.
/// </summary>
public readonly struct ActionSignature
{
    public string SignatureId { get; init; }
    public int TokenId { get; init; }
    public ActionId Action { get; init; }
    public float QValue { get; init; }
    public float AuthScore { get; init; }
    public int? PrototypeId { get; init; }
    public DateTime Timestamp { get; init; }
    public ChainScores ChainScores { get; init; }
}
