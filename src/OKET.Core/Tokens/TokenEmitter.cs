using OKET.Core.State;
using OKET.Core.Types;
using OKET.Core.Detection;

namespace OKET.Core.Tokens;

/// <summary>
/// Emits perception tokens from game state.
/// CRITICAL: Tokenization is LOSSY on purpose.
///
/// Only emit tokens when:
/// 1. State crosses a meaningful threshold
/// 2. Something changes decision-relevantly
/// 3. A new entity appears or disappears
///
/// If everything is a token, nothing is meaningful.
/// </summary>
public sealed class TokenEmitter
{
    private readonly TokenEmitterConfig _config;
    private readonly Dictionary<int, EntityState> _trackedEntities = new();
    private SelfStateSnapshot _lastSelfState;
    private long _lastFrameId;

    // Threshold tracking
    private int _lastHealthBucket;
    private int _lastAmmoBucket;
    private int _lastThreatCountBucket;

    public TokenEmitter(TokenEmitterConfig? config = null)
    {
        _config = config ?? new TokenEmitterConfig();
    }

    /// <summary>
    /// Emit tokens for current frame. Only significant changes produce tokens.
    /// </summary>
    public IEnumerable<PerceptionToken> EmitTokens(GameState state)
    {
        var tokens = new List<PerceptionToken>();
        var now = DateTime.UtcNow;

        // === SELF STATE TOKENS (only on threshold crossing) ===
        EmitSelfStateTokens(state, tokens, now);

        // === ENEMY TOKENS (only significant changes) ===
        EmitEnemyTokens(state, tokens, now);

        // === ITEM TOKENS (only new items or item changes) ===
        EmitItemTokens(state, tokens, now);

        // === NAVIGATION TOKENS (only on state change) ===
        EmitNavigationTokens(state, tokens, now);

        _lastFrameId = state.FrameId;
        return tokens.Where(t => t.IsSignificant);
    }

    private void EmitSelfStateTokens(GameState state, List<PerceptionToken> tokens, DateTime now)
    {
        // Health - only emit on bucket change
        int healthBucket = GetHealthBucket(state.Hud.Health);
        if (healthBucket != _lastHealthBucket)
        {
            tokens.Add(new PerceptionToken
            {
                SchemaVersion = TokenSchemaVersion.Version,
                Type = TokenType.SelfState,
                Value = GetHealthValue(healthBucket),
                Confidence = 1f,
                Timestamp = now,
                FrameId = state.FrameId,
                Urgency = (byte)(healthBucket <= 1 ? 10 : 5 - healthBucket),
                IsSignificant = true
            });
            _lastHealthBucket = healthBucket;
        }

        // Ammo - only emit on bucket change
        int ammoBucket = GetAmmoBucket(state.Hud.Ammo, state.Hud.MaxAmmo);
        if (ammoBucket != _lastAmmoBucket)
        {
            tokens.Add(new PerceptionToken
            {
                SchemaVersion = TokenSchemaVersion.Version,
                Type = TokenType.SelfState,
                Value = GetAmmoValue(ammoBucket),
                Confidence = 1f,
                Timestamp = now,
                FrameId = state.FrameId,
                Urgency = (byte)(ammoBucket == 0 ? 8 : 3),
                IsSignificant = true
            });
            _lastAmmoBucket = ammoBucket;
        }

        // Threat count - only emit on bucket change
        int threatBucket = GetThreatCountBucket(state.ThreatsInFov);
        if (threatBucket != _lastThreatCountBucket)
        {
            tokens.Add(new PerceptionToken
            {
                SchemaVersion = TokenSchemaVersion.Version,
                Type = TokenType.Environment,
                Value = GetThreatCountValue(threatBucket),
                Confidence = 1f,
                Timestamp = now,
                FrameId = state.FrameId,
                Urgency = (byte)Math.Min(10, threatBucket * 2),
                IsSignificant = true
            });
            _lastThreatCountBucket = threatBucket;
        }
    }

    private void EmitEnemyTokens(GameState state, List<PerceptionToken> tokens, DateTime now)
    {
        var currentIds = new HashSet<int>();

        foreach (var detection in state.Detections.Detections.Where(d => d.IsThreat))
        {
            currentIds.Add(detection.TrackId);

            bool isNew = !_trackedEntities.ContainsKey(detection.TrackId);
            bool significantChange = false;

            if (!isNew)
            {
                var prev = _trackedEntities[detection.TrackId];
                // Only significant if distance bucket changed or velocity changed significantly
                int prevDistBucket = GetDistanceBucket(prev.Distance);
                int currDistBucket = GetDistanceBucket(detection.EstimatedDistance ?? 50f);
                significantChange = prevDistBucket != currDistBucket ||
                    (detection.Velocity?.Length ?? 0) > prev.Velocity + 20f;
            }

            if (isNew || significantChange)
            {
                var distBucket = GetDistanceBucket(detection.EstimatedDistance ?? 50f);
                tokens.Add(new PerceptionToken
                {
                    SchemaVersion = TokenSchemaVersion.Version,
                    Type = TokenType.Enemy,
                    Value = $"{detection.Class}_{GetDistanceLabel(distBucket)}",
                    Confidence = detection.Confidence,
                    Distance = detection.EstimatedDistance,
                    DistanceSource = DistanceSource.BoundingBox,
                    Velocity = detection.Velocity,
                    ScreenPosition = detection.Box.Center,
                    Timestamp = now,
                    FrameId = state.FrameId,
                    Urgency = (byte)(10 - distBucket * 2),
                    IsSignificant = isNew || significantChange
                });
            }

            // Update tracking
            _trackedEntities[detection.TrackId] = new EntityState
            {
                Distance = detection.EstimatedDistance ?? 50f,
                Velocity = detection.Velocity?.Length ?? 0f,
                LastSeen = state.FrameId
            };
        }

        // Emit tokens for entities that disappeared (significant!)
        var disappeared = _trackedEntities.Keys.Except(currentIds).ToList();
        foreach (var id in disappeared)
        {
            if (state.FrameId - _trackedEntities[id].LastSeen > 30) // Gone for 1 second
            {
                tokens.Add(new PerceptionToken
                {
                    SchemaVersion = TokenSchemaVersion.Version,
                    Type = TokenType.Enemy,
                    Value = "enemy_lost",
                    Confidence = 0.8f,
                    Timestamp = now,
                    FrameId = state.FrameId,
                    Urgency = 3,
                    IsSignificant = true
                });
                _trackedEntities.Remove(id);
            }
        }
    }

    private void EmitItemTokens(GameState state, List<PerceptionToken> tokens, DateTime now)
    {
        foreach (var detection in state.Detections.Detections.Where(d => d.IsInteractable))
        {
            bool isNew = !_trackedEntities.ContainsKey(detection.TrackId);

            if (isNew)
            {
                var distBucket = GetDistanceBucket(detection.EstimatedDistance ?? 50f);
                tokens.Add(new PerceptionToken
                {
                    SchemaVersion = TokenSchemaVersion.Version,
                    Type = TokenType.Item,
                    Value = $"{detection.Class}_{GetDistanceLabel(distBucket)}",
                    Confidence = detection.Confidence,
                    Distance = detection.EstimatedDistance,
                    DistanceSource = DistanceSource.BoundingBox,
                    ScreenPosition = detection.Box.Center,
                    Timestamp = now,
                    FrameId = state.FrameId,
                    Urgency = GetItemUrgency(detection.Class, state),
                    IsSignificant = true
                });

                _trackedEntities[detection.TrackId] = new EntityState
                {
                    Distance = detection.EstimatedDistance ?? 50f,
                    LastSeen = state.FrameId
                };
            }
        }
    }

    private void EmitNavigationTokens(GameState state, List<PerceptionToken> tokens, DateTime now)
    {
        // Navigation tokens would come from NavigationPolicy state changes
        // Only emit when: path blocked, goal reached, new path found
        // This is a placeholder for integration with NavigationPolicy
    }

    // === Bucketing functions (lossy quantization) ===

    private static int GetHealthBucket(int health) => health switch
    {
        <= 0 => 0,   // Dead
        < 20 => 1,   // Critical
        < 50 => 2,   // Low
        < 80 => 3,   // Medium
        _ => 4       // High
    };

    private static string GetHealthValue(int bucket) => bucket switch
    {
        0 => "health_dead",
        1 => "health_critical",
        2 => "health_low",
        3 => "health_medium",
        _ => "health_high"
    };

    private static int GetAmmoBucket(int ammo, int maxAmmo)
    {
        if (maxAmmo <= 0) return 0;
        float ratio = (float)ammo / maxAmmo;
        return ratio switch
        {
            <= 0 => 0,
            < 0.25f => 1,
            < 0.50f => 2,
            _ => 3
        };
    }

    private static string GetAmmoValue(int bucket) => bucket switch
    {
        0 => "ammo_empty",
        1 => "ammo_low",
        2 => "ammo_medium",
        _ => "ammo_ok"
    };

    private static int GetThreatCountBucket(int count) => count switch
    {
        0 => 0,
        <= 2 => 1,
        <= 5 => 2,
        <= 10 => 3,
        _ => 4
    };

    private static string GetThreatCountValue(int bucket) => bucket switch
    {
        0 => "threats_none",
        1 => "threats_few",
        2 => "threats_some",
        3 => "threats_many",
        _ => "threats_swarm"
    };

    private static int GetDistanceBucket(float distance) => distance switch
    {
        < 2f => 0,   // Contact
        < 5f => 1,   // Close
        < 15f => 2,  // Medium
        < 30f => 3,  // Far
        _ => 4       // Very far
    };

    private static string GetDistanceLabel(int bucket) => bucket switch
    {
        0 => "contact",
        1 => "close",
        2 => "medium",
        3 => "far",
        _ => "distant"
    };

    private static byte GetItemUrgency(DetectionClass itemClass, GameState state)
    {
        return itemClass switch
        {
            DetectionClass.HealthKit when state.Hud.Health < 50 => 8,
            DetectionClass.AmmoCrate when state.Hud.Ammo < state.Hud.MaxAmmo / 4 => 7,
            DetectionClass.WeaponCrate => 5,
            _ => 3
        };
    }

    private struct EntityState
    {
        public float Distance;
        public float Velocity;
        public long LastSeen;
    }

    private struct SelfStateSnapshot
    {
        public int Health;
        public int Ammo;
        public int ThreatCount;
    }
}

/// <summary>
/// Configuration for token emission thresholds.
/// </summary>
public sealed class TokenEmitterConfig
{
    /// <summary>Minimum frames between same-entity token updates.</summary>
    public int MinFramesBetweenUpdates { get; init; } = 15;

    /// <summary>Distance change threshold to trigger token (meters).</summary>
    public float DistanceChangeThreshold { get; init; } = 2f;

    /// <summary>Velocity change threshold to trigger token (px/frame).</summary>
    public float VelocityChangeThreshold { get; init; } = 10f;
}
