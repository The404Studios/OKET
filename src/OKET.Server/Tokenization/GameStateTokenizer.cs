using System.Text.Json;
using OKET.Core.State;
using OKET.Core.Types;

namespace OKET.Server.Tokenization;

/// <summary>
/// Tokenizes game state into structured tokens for analysis and streaming.
/// Converts continuous game data into discrete, meaningful tokens.
/// </summary>
public sealed class GameStateTokenizer
{
    private readonly TokenizerConfig _config;
    private long _lastTokenizedFrame;
    private GameStateToken? _lastToken;

    public GameStateTokenizer(TokenizerConfig? config = null)
    {
        _config = config ?? new TokenizerConfig();
    }

    /// <summary>
    /// Tokenize a game state into discrete tokens.
    /// </summary>
    public GameStateToken Tokenize(GameState state)
    {
        var token = new GameStateToken
        {
            FrameId = state.FrameId,
            Timestamp = state.Timestamp,

            // Player state tokens
            HealthToken = QuantizeHealth(state.Hud.Health),
            AmmoToken = QuantizeAmmo(state.Hud.Ammo, state.Hud.MaxAmmo),
            PositionToken = QuantizePosition(state.ScreenSize / 2f, state.ScreenSize),

            // Threat tokens
            ThreatCountToken = QuantizeThreatCount(state.ThreatsInFov),
            NearestThreatToken = QuantizeDistance(state.NearestThreatDistance),
            ThreatDirectionToken = GetThreatDirection(state),

            // Action state tokens
            AimStateToken = GetAimStateToken(state.Aim),
            MovementStateToken = GetMovementStateToken(state),

            // Context tokens
            EnvironmentToken = GetEnvironmentToken(state),
            UrgencyToken = CalculateUrgencyToken(state)
        };

        // Calculate delta tokens if we have history
        if (_lastToken != null && state.FrameId - _lastTokenizedFrame < 30)
        {
            token.HealthDeltaToken = GetDeltaToken(token.HealthToken, _lastToken.HealthToken);
            token.ThreatDeltaToken = GetDeltaToken(token.ThreatCountToken, _lastToken.ThreatCountToken);
        }

        _lastToken = token;
        _lastTokenizedFrame = state.FrameId;

        return token;
    }

    /// <summary>
    /// Convert token to compact JSON for streaming.
    /// </summary>
    public string ToJson(GameStateToken token)
    {
        return JsonSerializer.Serialize(token, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    /// <summary>
    /// Convert token to compact byte array for efficient streaming.
    /// </summary>
    public byte[] ToBytes(GameStateToken token)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(token.FrameId);
        writer.Write(token.Timestamp.ToBinary());
        writer.Write(token.HealthToken);
        writer.Write(token.AmmoToken);
        writer.Write(token.PositionToken);
        writer.Write(token.ThreatCountToken);
        writer.Write(token.NearestThreatToken);
        writer.Write(token.ThreatDirectionToken);
        writer.Write(token.AimStateToken);
        writer.Write(token.MovementStateToken);
        writer.Write(token.EnvironmentToken);
        writer.Write(token.UrgencyToken);
        writer.Write(token.HealthDeltaToken);
        writer.Write(token.ThreatDeltaToken);

        return stream.ToArray();
    }

    /// <summary>
    /// Parse token from bytes.
    /// </summary>
    public GameStateToken FromBytes(byte[] data)
    {
        using var stream = new MemoryStream(data);
        using var reader = new BinaryReader(stream);

        return new GameStateToken
        {
            FrameId = reader.ReadInt64(),
            Timestamp = DateTime.FromBinary(reader.ReadInt64()),
            HealthToken = reader.ReadByte(),
            AmmoToken = reader.ReadByte(),
            PositionToken = reader.ReadByte(),
            ThreatCountToken = reader.ReadByte(),
            NearestThreatToken = reader.ReadByte(),
            ThreatDirectionToken = reader.ReadByte(),
            AimStateToken = reader.ReadByte(),
            MovementStateToken = reader.ReadByte(),
            EnvironmentToken = reader.ReadByte(),
            UrgencyToken = reader.ReadByte(),
            HealthDeltaToken = reader.ReadSByte(),
            ThreatDeltaToken = reader.ReadSByte()
        };
    }

    // Quantization methods - convert continuous values to discrete tokens

    private byte QuantizeHealth(int health)
    {
        // 0: Dead, 1: Critical (<20), 2: Low (<50), 3: Medium (<80), 4: High (>=80)
        return health switch
        {
            <= 0 => 0,
            < 20 => 1,
            < 50 => 2,
            < 80 => 3,
            _ => 4
        };
    }

    private byte QuantizeAmmo(int ammo, int maxAmmo)
    {
        if (maxAmmo <= 0) return 0;
        float ratio = (float)ammo / maxAmmo;
        // 0: Empty, 1: Low (<25%), 2: Medium (<50%), 3: High (<75%), 4: Full (>=75%)
        return ratio switch
        {
            <= 0 => 0,
            < 0.25f => 1,
            < 0.50f => 2,
            < 0.75f => 3,
            _ => 4
        };
    }

    private byte QuantizePosition(Vector2 position, Vector2 screenSize)
    {
        // Divide screen into 9 zones (3x3 grid)
        int x = (int)(position.X / screenSize.X * 3);
        int y = (int)(position.Y / screenSize.Y * 3);
        x = Math.Clamp(x, 0, 2);
        y = Math.Clamp(y, 0, 2);
        return (byte)(y * 3 + x); // 0-8
    }

    private byte QuantizeThreatCount(int count)
    {
        // 0: None, 1: Few (1-2), 2: Some (3-5), 3: Many (6-10), 4: Swarm (>10)
        return count switch
        {
            0 => 0,
            <= 2 => 1,
            <= 5 => 2,
            <= 10 => 3,
            _ => 4
        };
    }

    private byte QuantizeDistance(float distance)
    {
        // 0: Contact (<50), 1: Close (<150), 2: Medium (<300), 3: Far (<500), 4: VeryFar (>=500)
        return distance switch
        {
            < 50 => 0,
            < 150 => 1,
            < 300 => 2,
            < 500 => 3,
            _ => 4
        };
    }

    private byte GetThreatDirection(GameState state)
    {
        var threat = state.Detections.PrimaryThreat;
        if (threat == null) return 8; // No threat = center

        var center = state.ScreenSize / 2f;
        var dir = threat.Box.Center - center;

        // Convert to 8 directions + center
        float angle = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;
        angle = (angle + 180f + 22.5f) % 360f; // Normalize to 0-360 with offset

        return (byte)(angle / 45f); // 0-7 for directions, 8 for center/none
    }

    private byte GetAimStateToken(AimState aim)
    {
        // Combine on-target and tracking quality
        if (aim.IsOnTarget) return 4;
        if (aim.TrackingQuality > 0.8f) return 3;
        if (aim.TrackingQuality > 0.5f) return 2;
        if (aim.TrackingQuality > 0.2f) return 1;
        return 0;
    }

    private byte GetMovementStateToken(GameState state)
    {
        // Encode movement state: 0=idle, 1-4=cardinal, 5-8=diagonal
        // This would need actual movement input tracking
        return 0; // Placeholder - would be set from input state
    }

    private byte GetEnvironmentToken(GameState state)
    {
        // Encode environment context
        // 0: Open, 1: Corridor, 2: Room, 3: Outdoors, 4: Combat zone
        int threats = state.ThreatsInFov;
        if (threats > 5) return 4; // Combat zone
        if (threats > 0) return 3; // Active area
        return 0; // Default
    }

    private byte CalculateUrgencyToken(GameState state)
    {
        // Combine multiple factors into urgency level
        int urgency = 0;

        if (state.Hud.Health < 20) urgency += 3;
        else if (state.Hud.Health < 50) urgency += 1;

        if (state.NearestThreatDistance < 100) urgency += 3;
        else if (state.NearestThreatDistance < 200) urgency += 2;
        else if (state.NearestThreatDistance < 300) urgency += 1;

        if (state.ThreatsInFov > 5) urgency += 2;
        else if (state.ThreatsInFov > 2) urgency += 1;

        if (state.Hud.Ammo == 0 && state.ThreatsInFov > 0) urgency += 2;

        return (byte)Math.Clamp(urgency, 0, 10);
    }

    private sbyte GetDeltaToken(byte current, byte previous)
    {
        return (sbyte)(current - previous);
    }
}

/// <summary>
/// Configuration for tokenizer behavior.
/// </summary>
public sealed class TokenizerConfig
{
    /// <summary>How many frames to skip between tokenizations.</summary>
    public int FrameSkip { get; init; } = 1;

    /// <summary>Include raw position data in tokens.</summary>
    public bool IncludeRawPositions { get; init; }

    /// <summary>Include detailed detection data.</summary>
    public bool IncludeDetections { get; init; }
}

/// <summary>
/// Tokenized game state - discrete representation of continuous game data.
/// Each token is a small integer representing a quantized value.
/// </summary>
public sealed class GameStateToken
{
    public long FrameId { get; init; }
    public DateTime Timestamp { get; init; }

    // Player state (0-4 scale)
    public byte HealthToken { get; init; }
    public byte AmmoToken { get; init; }
    public byte PositionToken { get; init; } // 0-8 screen zone

    // Threat state
    public byte ThreatCountToken { get; init; } // 0-4 scale
    public byte NearestThreatToken { get; init; } // 0-4 distance
    public byte ThreatDirectionToken { get; init; } // 0-8 direction

    // Action state
    public byte AimStateToken { get; init; } // 0-4 quality
    public byte MovementStateToken { get; init; } // 0-8 direction

    // Context
    public byte EnvironmentToken { get; init; }
    public byte UrgencyToken { get; init; } // 0-10 scale

    // Deltas from previous frame
    public sbyte HealthDeltaToken { get; set; }
    public sbyte ThreatDeltaToken { get; set; }

    /// <summary>
    /// Get token as a fixed-size vector for ML input.
    /// </summary>
    public float[] ToVector()
    {
        return new float[]
        {
            HealthToken / 4f,
            AmmoToken / 4f,
            PositionToken / 8f,
            ThreatCountToken / 4f,
            NearestThreatToken / 4f,
            ThreatDirectionToken / 8f,
            AimStateToken / 4f,
            MovementStateToken / 8f,
            EnvironmentToken / 4f,
            UrgencyToken / 10f,
            (HealthDeltaToken + 4) / 8f,
            (ThreatDeltaToken + 4) / 8f
        };
    }

    /// <summary>
    /// Create a compact string representation.
    /// </summary>
    public string ToCompactString()
    {
        return $"H{HealthToken}A{AmmoToken}T{ThreatCountToken}D{NearestThreatToken}U{UrgencyToken}";
    }
}
