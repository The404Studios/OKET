namespace OKET.Core.Types;

/// <summary>
/// 2D vector for screen coordinates, offsets, and velocities.
/// </summary>
public readonly struct Vector2 : IEquatable<Vector2>
{
    public float X { get; }
    public float Y { get; }

    public Vector2(float x, float y)
    {
        X = x;
        Y = y;
    }

    public static Vector2 Zero => new(0, 0);
    public static Vector2 One => new(1, 1);

    public float Length => MathF.Sqrt(X * X + Y * Y);
    public float LengthSquared => X * X + Y * Y;

    public Vector2 Normalized => Length > 0 ? this / Length : Zero;

    public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vector2 operator *(Vector2 v, float s) => new(v.X * s, v.Y * s);
    public static Vector2 operator /(Vector2 v, float s) => new(v.X / s, v.Y / s);
    public static Vector2 operator -(Vector2 v) => new(-v.X, -v.Y);

    public static float Dot(Vector2 a, Vector2 b) => a.X * b.X + a.Y * b.Y;
    public static float Distance(Vector2 a, Vector2 b) => (a - b).Length;

    public static Vector2 Lerp(Vector2 a, Vector2 b, float t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    public bool Equals(Vector2 other) =>
        Math.Abs(X - other.X) < 0.0001f && Math.Abs(Y - other.Y) < 0.0001f;

    public override bool Equals(object? obj) => obj is Vector2 other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public override string ToString() => $"({X:F2}, {Y:F2})";

    public static bool operator ==(Vector2 left, Vector2 right) => left.Equals(right);
    public static bool operator !=(Vector2 left, Vector2 right) => !left.Equals(right);
}
