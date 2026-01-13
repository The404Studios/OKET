namespace OKET.Core.Types;

/// <summary>
/// A captured frame from the game window.
/// </summary>
public sealed class Frame : IDisposable
{
    /// <summary>Frame ID (monotonically increasing).</summary>
    public long Id { get; }

    /// <summary>Timestamp when frame was captured.</summary>
    public DateTime Timestamp { get; }

    /// <summary>Frame width in pixels.</summary>
    public int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// Raw pixel data in BGRA format.
    /// Layout: [B, G, R, A, B, G, R, A, ...] row by row.
    /// </summary>
    public byte[] Data { get; }

    /// <summary>Stride (bytes per row).</summary>
    public int Stride => Width * 4;

    public Frame(long id, DateTime timestamp, int width, int height, byte[] data)
    {
        Id = id;
        Timestamp = timestamp;
        Width = width;
        Height = height;
        Data = data;
    }

    /// <summary>
    /// Get pixel color at the specified coordinates.
    /// </summary>
    public (byte B, byte G, byte R, byte A) GetPixel(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            return (0, 0, 0, 0);

        int offset = y * Stride + x * 4;
        return (Data[offset], Data[offset + 1], Data[offset + 2], Data[offset + 3]);
    }

    /// <summary>
    /// Extract a rectangular region as a new frame.
    /// </summary>
    public Frame Crop(int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        width = Math.Min(width, Width - x);
        height = Math.Min(height, Height - y);

        var croppedData = new byte[width * height * 4];

        for (int row = 0; row < height; row++)
        {
            int srcOffset = (y + row) * Stride + x * 4;
            int dstOffset = row * width * 4;
            Array.Copy(Data, srcOffset, croppedData, dstOffset, width * 4);
        }

        return new Frame(Id, Timestamp, width, height, croppedData);
    }

    /// <summary>
    /// Create a deep copy of this frame.
    /// </summary>
    public Frame Clone()
    {
        var clonedData = new byte[Data.Length];
        Array.Copy(Data, clonedData, Data.Length);
        return new Frame(Id, Timestamp, Width, Height, clonedData);
    }

    public void Dispose()
    {
        // Data is managed, no explicit disposal needed
        // But this allows for future pooling optimizations
    }
}
