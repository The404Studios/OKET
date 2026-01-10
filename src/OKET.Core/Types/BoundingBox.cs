namespace OKET.Core.Types;

/// <summary>
/// Axis-aligned bounding box for detected objects.
/// </summary>
public readonly struct BoundingBox : IEquatable<BoundingBox>
{
    /// <summary>Top-left X coordinate (pixels).</summary>
    public float X { get; }

    /// <summary>Top-left Y coordinate (pixels).</summary>
    public float Y { get; }

    /// <summary>Width in pixels.</summary>
    public float Width { get; }

    /// <summary>Height in pixels.</summary>
    public float Height { get; }

    public BoundingBox(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public float Left => X;
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;

    public Vector2 Center => new(X + Width / 2, Y + Height / 2);
    public Vector2 TopLeft => new(X, Y);
    public Vector2 BottomRight => new(Right, Bottom);
    public Vector2 Size => new(Width, Height);

    public float Area => Width * Height;

    /// <summary>
    /// Returns a point at the given relative position within the box.
    /// (0,0) = top-left, (1,1) = bottom-right, (0.5, 0.3) = center-x, 30% down from top.
    /// </summary>
    public Vector2 GetPoint(float relativeX, float relativeY) =>
        new(X + Width * relativeX, Y + Height * relativeY);

    /// <summary>
    /// Get the headshot target point (top-center of box).
    /// </summary>
    public Vector2 HeadTarget => GetPoint(0.5f, 0.15f);

    /// <summary>
    /// Get the center-mass target point.
    /// </summary>
    public Vector2 BodyTarget => GetPoint(0.5f, 0.4f);

    public bool Contains(Vector2 point) =>
        point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;

    public bool Intersects(BoundingBox other) =>
        !(other.Left > Right || other.Right < Left || other.Top > Bottom || other.Bottom < Top);

    public static BoundingBox FromCenterSize(Vector2 center, Vector2 size) =>
        new(center.X - size.X / 2, center.Y - size.Y / 2, size.X, size.Y);

    public bool Equals(BoundingBox other) =>
        Math.Abs(X - other.X) < 0.1f &&
        Math.Abs(Y - other.Y) < 0.1f &&
        Math.Abs(Width - other.Width) < 0.1f &&
        Math.Abs(Height - other.Height) < 0.1f;

    public override bool Equals(object? obj) => obj is BoundingBox other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);
    public override string ToString() => $"[{X:F0},{Y:F0} {Width:F0}x{Height:F0}]";

    public static bool operator ==(BoundingBox left, BoundingBox right) => left.Equals(right);
    public static bool operator !=(BoundingBox left, BoundingBox right) => !left.Equals(right);
}
