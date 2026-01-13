using OKET.Core.Types;
using OKET.Core.State;
using OKET.Core.Tokens;

namespace OKET.Core.Policy;

/// <summary>
/// LOCK 2: Intent → Policy → Skill → Action Pipeline
///
/// This enforces strict separation of concerns:
///
/// Intent = WHAT we want (goal, not how to achieve it)
/// Policy = HOW we pursue intent (skill selection, priority, parameters)
/// Skill  = Concrete capability execution (navigation, aiming, etc.)
/// Action = Mechanical output (keypress, mouse move)
///
/// Rules:
/// 1. Intent never specifies HOW
/// 2. Policy never executes actions directly
/// 3. Skills never choose their own goals
/// 4. Actions are stateless mechanical outputs
/// </summary>
public sealed class IntentPolicyPipeline
{
    private readonly IntentSelector _intentSelector;
    private readonly PolicyResolver _policyResolver;

    /// <summary>Current high-level intent.</summary>
    public Intent CurrentIntent { get; private set; } = Intent.Idle;

    /// <summary>Current policy executing the intent.</summary>
    public ActivePolicy? CurrentPolicy { get; private set; }

    /// <summary>Frame when intent last changed.</summary>
    public long IntentChangedFrame { get; private set; }

    /// <summary>Reason for current intent.</summary>
    public string IntentReason { get; private set; } = "";

    public IntentPolicyPipeline()
    {
        _intentSelector = new IntentSelector();
        _policyResolver = new PolicyResolver();
    }

    /// <summary>
    /// Update intent based on current state and perception tokens.
    /// </summary>
    public IntentDecision Update(GameState state, IEnumerable<PerceptionToken> tokens)
    {
        // Step 1: Select intent based on state
        var intentResult = _intentSelector.SelectIntent(state, tokens);

        // Step 2: Check if intent changed
        bool intentChanged = intentResult.Intent != CurrentIntent;
        if (intentChanged)
        {
            CurrentIntent = intentResult.Intent;
            IntentChangedFrame = state.FrameId;
            IntentReason = intentResult.Reason;
        }

        // Step 3: Resolve policy for current intent
        CurrentPolicy = _policyResolver.ResolvePolicy(CurrentIntent, state, intentResult);

        return new IntentDecision
        {
            Intent = CurrentIntent,
            IntentChanged = intentChanged,
            Reason = IntentReason,
            Policy = CurrentPolicy,
            Confidence = intentResult.Confidence
        };
    }

    /// <summary>
    /// Get current pipeline state for overlay display.
    /// </summary>
    public PipelineState GetState()
    {
        return new PipelineState
        {
            Intent = CurrentIntent,
            IntentReason = IntentReason,
            PolicyName = CurrentPolicy?.Name ?? "None",
            PolicySkill = CurrentPolicy?.ActiveSkill ?? "None",
            Confidence = CurrentPolicy?.Confidence ?? 0f
        };
    }
}

/// <summary>
/// High-level intents - WHAT the agent wants.
/// These are goals, not methods.
/// </summary>
public enum Intent : byte
{
    /// <summary>No active goal.</summary>
    Idle = 0,

    /// <summary>Stay alive - highest priority when health low or overwhelmed.</summary>
    Survive = 1,

    /// <summary>Engage and eliminate threats.</summary>
    EngageEnemy = 2,

    /// <summary>Move to a specific location.</summary>
    ReachTarget = 3,

    /// <summary>Acquire an item (ammo, health, weapon).</summary>
    AcquireItem = 4,

    /// <summary>Avoid an immediate threat without engaging.</summary>
    AvoidThreat = 5,

    /// <summary>Support/follow a teammate.</summary>
    Support = 6,

    /// <summary>Defend a position.</summary>
    Defend = 7,

    /// <summary>Explore unknown area.</summary>
    Explore = 8
}

/// <summary>
/// Result of intent selection.
/// </summary>
public readonly struct IntentResult
{
    public Intent Intent { get; init; }
    public float Confidence { get; init; }
    public string Reason { get; init; }
    public Vector2? TargetPosition { get; init; }
    public int? TargetEntityId { get; init; }
    public float Priority { get; init; }
}

/// <summary>
/// Active policy executing an intent.
/// </summary>
public sealed class ActivePolicy
{
    /// <summary>Policy name for debugging.</summary>
    public string Name { get; init; } = "";

    /// <summary>Intent this policy serves.</summary>
    public Intent Intent { get; init; }

    /// <summary>Currently active skill.</summary>
    public string ActiveSkill { get; init; } = "";

    /// <summary>Skill parameters.</summary>
    public PolicyParameters Parameters { get; init; } = new();

    /// <summary>Confidence in policy success [0, 1].</summary>
    public float Confidence { get; init; }

    /// <summary>Whether policy is interruptible.</summary>
    public bool IsInterruptible { get; init; } = true;

    /// <summary>Maximum frames to execute before timeout.</summary>
    public int TimeoutFrames { get; init; } = 300;
}

/// <summary>
/// Parameters passed to skills by policy.
/// </summary>
public sealed class PolicyParameters
{
    public Vector2? TargetPosition { get; init; }
    public int? TargetEntityId { get; init; }
    public float Aggression { get; init; } = 0.5f;
    public float Caution { get; init; } = 0.5f;
    public bool AllowFire { get; init; } = true;
    public bool AllowMove { get; init; } = true;
}

/// <summary>
/// Decision output from pipeline.
/// </summary>
public readonly struct IntentDecision
{
    public Intent Intent { get; init; }
    public bool IntentChanged { get; init; }
    public string Reason { get; init; }
    public ActivePolicy? Policy { get; init; }
    public float Confidence { get; init; }
}

/// <summary>
/// Pipeline state for overlay display.
/// </summary>
public readonly struct PipelineState
{
    public Intent Intent { get; init; }
    public string IntentReason { get; init; }
    public string PolicyName { get; init; }
    public string PolicySkill { get; init; }
    public float Confidence { get; init; }
}

/// <summary>
/// Selects intent based on game state.
/// Intent = WHAT we want, never HOW.
/// </summary>
internal sealed class IntentSelector
{
    public IntentResult SelectIntent(GameState state, IEnumerable<PerceptionToken> tokens)
    {
        // Priority 1: Survive if in danger
        if (state.Hud.IsLowHealth && state.ThreatsInFov > 0)
        {
            return new IntentResult
            {
                Intent = Intent.Survive,
                Confidence = 0.9f,
                Reason = "Low health with active threats",
                Priority = 1.0f
            };
        }

        // Priority 2: Acquire health if low and safe
        if (state.Hud.Health < 50 && state.ThreatsInFov == 0)
        {
            var healthItem = state.Detections.Detections
                .FirstOrDefault(d => d.Class == Detection.DetectionClass.HealthKit);
            if (healthItem != null)
            {
                return new IntentResult
                {
                    Intent = Intent.AcquireItem,
                    Confidence = 0.8f,
                    Reason = "Need health, area safe",
                    TargetPosition = healthItem.Box.Center,
                    TargetEntityId = healthItem.TrackId,
                    Priority = 0.9f
                };
            }
        }

        // Priority 3: Engage if threats present and healthy
        if (state.ThreatsInFov > 0 && state.Hud.Health >= 50)
        {
            var threat = state.Detections.PrimaryThreat;
            return new IntentResult
            {
                Intent = Intent.EngageEnemy,
                Confidence = 0.85f,
                Reason = $"Engaging {state.ThreatsInFov} threats",
                TargetPosition = threat?.Box.Center,
                TargetEntityId = threat?.TrackId,
                Priority = 0.8f
            };
        }

        // Priority 4: Acquire ammo if low
        if (state.Hud.Ammo < state.Hud.MaxAmmo / 4)
        {
            var ammoItem = state.Detections.Detections
                .FirstOrDefault(d => d.Class == Detection.DetectionClass.AmmoCrate);
            if (ammoItem != null)
            {
                return new IntentResult
                {
                    Intent = Intent.AcquireItem,
                    Confidence = 0.7f,
                    Reason = "Low ammo",
                    TargetPosition = ammoItem.Box.Center,
                    TargetEntityId = ammoItem.TrackId,
                    Priority = 0.6f
                };
            }
        }

        // Priority 5: Avoid if threats present but low health
        if (state.ThreatsInFov > 0 && state.Hud.Health < 30)
        {
            return new IntentResult
            {
                Intent = Intent.AvoidThreat,
                Confidence = 0.85f,
                Reason = "Too weak to engage",
                Priority = 0.95f
            };
        }

        // Default: Explore or idle
        return new IntentResult
        {
            Intent = Intent.Explore,
            Confidence = 0.5f,
            Reason = "No immediate goals",
            Priority = 0.1f
        };
    }
}

/// <summary>
/// Resolves which policy to use for an intent.
/// Policy = HOW to pursue the intent.
/// </summary>
internal sealed class PolicyResolver
{
    public ActivePolicy ResolvePolicy(Intent intent, GameState state, IntentResult intentResult)
    {
        return intent switch
        {
            Intent.Survive => new ActivePolicy
            {
                Name = "SurvivalPolicy",
                Intent = intent,
                ActiveSkill = state.Hud.Ammo > 0 ? "CombatRetreat" : "Evade",
                Parameters = new PolicyParameters
                {
                    Caution = 1.0f,
                    Aggression = 0.2f,
                    AllowFire = state.Hud.Ammo > 0
                },
                Confidence = intentResult.Confidence,
                IsInterruptible = false,
                TimeoutFrames = 150
            },

            Intent.EngageEnemy => new ActivePolicy
            {
                Name = "CombatPolicy",
                Intent = intent,
                ActiveSkill = "AimAndFire",
                Parameters = new PolicyParameters
                {
                    TargetEntityId = intentResult.TargetEntityId,
                    TargetPosition = intentResult.TargetPosition,
                    Aggression = 0.8f,
                    Caution = 0.3f,
                    AllowFire = true
                },
                Confidence = intentResult.Confidence,
                TimeoutFrames = 300
            },

            Intent.AcquireItem => new ActivePolicy
            {
                Name = "AcquisitionPolicy",
                Intent = intent,
                ActiveSkill = "NavigateToTarget",
                Parameters = new PolicyParameters
                {
                    TargetEntityId = intentResult.TargetEntityId,
                    TargetPosition = intentResult.TargetPosition,
                    AllowFire = false,
                    AllowMove = true
                },
                Confidence = intentResult.Confidence,
                TimeoutFrames = 450
            },

            Intent.AvoidThreat => new ActivePolicy
            {
                Name = "AvoidancePolicy",
                Intent = intent,
                ActiveSkill = "Evade",
                Parameters = new PolicyParameters
                {
                    Caution = 1.0f,
                    Aggression = 0f,
                    AllowFire = false
                },
                Confidence = intentResult.Confidence,
                IsInterruptible = true,
                TimeoutFrames = 120
            },

            Intent.Explore => new ActivePolicy
            {
                Name = "ExplorationPolicy",
                Intent = intent,
                ActiveSkill = "Patrol",
                Parameters = new PolicyParameters
                {
                    Caution = 0.5f,
                    Aggression = 0.5f
                },
                Confidence = intentResult.Confidence,
                TimeoutFrames = 600
            },

            _ => new ActivePolicy
            {
                Name = "IdlePolicy",
                Intent = Intent.Idle,
                ActiveSkill = "Wait",
                Confidence = 0.5f,
                TimeoutFrames = 60
            }
        };
    }
}
