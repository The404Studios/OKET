using OKET.Core.State;
using OKET.Core.Detection;
using OKET.Core.Types;
using OKET.Core.Telemetry;

namespace OKET.Server.Tokenization;

/// <summary>
/// Converts game state to bounded telemetry tokens.
///
/// RULES (non-negotiable):
/// 1. Cap entities per tick (max 64)
/// 2. Cap UI texts per tick (max 16)
/// 3. Only emit on change or threshold crossing
/// 4. All coordinates normalized [0..1]
/// 5. All tokens include schema version
/// </summary>
public sealed class TelemetryTokenizer
{
    private SelfStateToken? _lastSelfState;
    private readonly Dictionary<int, EntityToken> _lastEntityStates = new();
    private long _lastTickId;

    // Thresholds for change detection
    private const float HealthChangeThreshold = 0.05f;  // 5% change
    private const float AmmoChangeThreshold = 0.1f;     // 10% change
    private const float PositionChangeThreshold = 0.02f; // 2% screen movement

    /// <summary>
    /// Tokenize game state into bounded telemetry tokens.
    /// Only emits tokens for significant changes.
    /// </summary>
    public IEnumerable<TelemetryToken> Tokenize(GameState state, Vector2 screenSize)
    {
        var tokens = new List<TelemetryToken>();
        long tickId = state.FrameId;

        // 1. Self state token (only on change)
        var selfToken = CreateSelfStateToken(state);
        if (ShouldEmitSelfState(selfToken))
        {
            tokens.Add(TelemetryToken.Create(tickId, TokenType.SelfState, selfToken, state.Hud.Confidence));
            _lastSelfState = selfToken;
        }

        // 2. Entity tokens (bounded to MaxEntitiesPerTick)
        var entityTokens = CreateEntityTokens(state, screenSize);
        int entityCount = 0;
        foreach (var (token, confidence) in entityTokens)
        {
            if (entityCount >= TelemetrySchema.MaxEntitiesPerTick)
                break;

            if (ShouldEmitEntity(token))
            {
                tokens.Add(TelemetryToken.Create(tickId, TokenType.Entity, token, confidence));
                _lastEntityStates[token.TrackId] = token;
                entityCount++;
            }
        }

        // 3. Prune stale entities
        PruneStaleEntities(state);

        _lastTickId = tickId;
        return tokens;
    }

    private SelfStateToken CreateSelfStateToken(GameState state)
    {
        return new SelfStateToken(
            Health01: state.Hud.Health / 100f,
            Armor01: state.Hud.Armor / 100f,
            Ammo01: state.Hud.MaxAmmo > 0 ? state.Hud.Ammo / (float)state.Hud.MaxAmmo : 0f,
            IsReloading: state.Hud.IsReloading,
            IsDead: state.Hud.IsDead,
            WaveNumber: state.Hud.Wave
        );
    }

    private bool ShouldEmitSelfState(SelfStateToken current)
    {
        if (_lastSelfState == null) return true;

        var last = _lastSelfState.Value;

        // Emit on threshold crossing
        if (MathF.Abs(current.Health01 - last.Health01) >= HealthChangeThreshold) return true;
        if (MathF.Abs(current.Ammo01 - last.Ammo01) >= AmmoChangeThreshold) return true;
        if (current.IsReloading != last.IsReloading) return true;
        if (current.IsDead != last.IsDead) return true;
        if (current.WaveNumber != last.WaveNumber) return true;

        return false;
    }

    private IEnumerable<(EntityToken token, float confidence)> CreateEntityTokens(GameState state, Vector2 screenSize)
    {
        // Sort by priority (threats first, then by distance)
        var sortedDetections = state.Detections.Detections
            .OrderByDescending(d => d.Priority)
            .ThenBy(d => d.EstimatedDistance ?? float.MaxValue)
            .Take(TelemetrySchema.MaxEntitiesPerTick);

        foreach (var det in sortedDetections)
        {
            var kind = MapDetectionClassToEntityKind(det.Class);
            var center = det.Box.Center;

            yield return (new EntityToken(
                Kind: kind,
                TrackId: det.TrackId,
                X: center.X / screenSize.X,
                Y: center.Y / screenSize.Y,
                W: det.Box.Width / screenSize.X,
                H: det.Box.Height / screenSize.Y,
                DistanceM: det.EstimatedDistance ?? -1f,
                Vx: 0f,  // TODO: velocity tracking
                Vy: 0f
            ), det.Confidence);
        }
    }

    private bool ShouldEmitEntity(EntityToken current)
    {
        if (!_lastEntityStates.TryGetValue(current.TrackId, out var last))
            return true; // New entity

        // Emit on significant position change
        float dx = MathF.Abs(current.X - last.X);
        float dy = MathF.Abs(current.Y - last.Y);
        if (dx >= PositionChangeThreshold || dy >= PositionChangeThreshold)
            return true;

        // Emit on size change (entity getting closer/farther)
        float sizeChange = MathF.Abs((current.W * current.H) - (last.W * last.H));
        if (sizeChange > 0.01f)
            return true;

        return false;
    }

    private void PruneStaleEntities(GameState state)
    {
        var currentIds = state.Detections.Detections.Select(d => d.TrackId).ToHashSet();
        var staleIds = _lastEntityStates.Keys.Where(id => !currentIds.Contains(id)).ToList();
        foreach (var id in staleIds)
        {
            _lastEntityStates.Remove(id);
        }
    }

    private static EntityKind MapDetectionClassToEntityKind(DetectionClass cls) => cls switch
    {
        DetectionClass.Zombie => EntityKind.Zombie,
        DetectionClass.ZombieHead => EntityKind.Zombie,
        DetectionClass.FastZombie => EntityKind.FastZombie,
        DetectionClass.PoisonZombie => EntityKind.PoisonZombie,
        DetectionClass.Headcrab => EntityKind.Headcrab,
        DetectionClass.Player => EntityKind.Player,
        DetectionClass.PlayerHead => EntityKind.Player,
        DetectionClass.Teammate => EntityKind.Teammate,
        DetectionClass.Survivor => EntityKind.Survivor,
        DetectionClass.SurvivorHead => EntityKind.Survivor,
        DetectionClass.HealthKit => EntityKind.HealthPack,
        DetectionClass.AmmoCrate => EntityKind.AmmoCrate,
        DetectionClass.WeaponCrate => EntityKind.WeaponCrate,
        _ => EntityKind.Unknown
    };

    /// <summary>
    /// Get statistics about tokenization.
    /// </summary>
    public TokenizerStats GetStats() => new()
    {
        TrackedEntities = _lastEntityStates.Count,
        LastTickId = _lastTickId,
        HasSelfState = _lastSelfState.HasValue
    };
}

public sealed class TokenizerStats
{
    public int TrackedEntities { get; init; }
    public long LastTickId { get; init; }
    public bool HasSelfState { get; init; }
}
