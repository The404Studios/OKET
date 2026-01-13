using System.Drawing;
using System.Drawing.Imaging;
using OKET.Core.Types;
using Tesseract;

namespace OKET.Vision.OCR;

/// <summary>
/// OCR text recognition with distance estimation.
/// Uses text size to estimate how far away text is in the game world.
/// </summary>
public sealed class TextRecognizer : IDisposable
{
    private TesseractEngine? _engine;
    private bool _disposed;
    private readonly string _tessDataPath;

    // Reference: Expected font height at 1 meter distance (calibrate per game)
    private const float ReferenceHeightAt1m = 40f; // pixels
    private const float FocalLength = 1000f; // virtual focal length for distance calc

    /// <summary>
    /// Whether the OCR engine is initialized.
    /// </summary>
    public bool IsInitialized => _engine != null;

    public TextRecognizer(string tessDataPath = "tessdata")
    {
        _tessDataPath = tessDataPath;
    }

    /// <summary>
    /// Initialize the Tesseract engine.
    /// </summary>
    public void Initialize()
    {
        if (_engine != null) return;

        try
        {
            _engine = new TesseractEngine(_tessDataPath, "eng", EngineMode.Default);
            _engine.SetVariable("tessedit_char_whitelist",
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789:/-_[]() ");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to initialize Tesseract. Ensure tessdata folder exists at: {_tessDataPath}", ex);
        }
    }

    /// <summary>
    /// Recognize text in an image with distance estimation.
    /// </summary>
    public TextRecognitionResult Recognize(Bitmap image)
    {
        if (_engine == null)
            throw new InvalidOperationException("TextRecognizer not initialized. Call Initialize() first.");

        var result = new TextRecognitionResult
        {
            Timestamp = DateTime.UtcNow,
            ImageWidth = image.Width,
            ImageHeight = image.Height
        };

        try
        {
            using var pix = PixConverter.ToPix(image);
            using var page = _engine.Process(pix);

            result.MeanConfidence = page.GetMeanConfidence();

            using var iter = page.GetIterator();
            iter.Begin();

            do
            {
                if (iter.TryGetBoundingBox(PageIteratorLevel.Word, out var bounds))
                {
                    var text = iter.GetText(PageIteratorLevel.Word)?.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var confidence = iter.GetConfidence(PageIteratorLevel.Word);
                    if (confidence < 50) continue; // Skip low confidence

                    var box = new BoundingBox(bounds.X1, bounds.Y1,
                        bounds.Width, bounds.Height);

                    // Estimate distance based on text height
                    var estimatedDistance = EstimateDistance(bounds.Height);

                    result.Words.Add(new RecognizedText
                    {
                        Text = text,
                        Confidence = confidence / 100f,
                        Box = box,
                        FontHeight = bounds.Height,
                        EstimatedDistance = estimatedDistance,
                        ScreenPosition = box.Center,
                        IsPlayerName = IsLikelyPlayerName(text),
                        IsGameText = IsLikelyGameText(text)
                    });
                }
            } while (iter.Next(PageIteratorLevel.Word));
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Recognize text in a specific region of the image.
    /// </summary>
    public TextRecognitionResult RecognizeRegion(Bitmap image, Rectangle region)
    {
        using var cropped = image.Clone(region, image.PixelFormat);
        var result = Recognize(cropped);

        // Adjust bounding boxes to full image coordinates
        foreach (var word in result.Words)
        {
            word.Box = new BoundingBox(
                word.Box.X + region.X,
                word.Box.Y + region.Y,
                word.Box.Width,
                word.Box.Height);
            word.ScreenPosition = word.Box.Center;
        }

        return result;
    }

    /// <summary>
    /// Estimate distance based on text height using perspective projection.
    /// Larger text = closer, smaller text = farther.
    /// </summary>
    private float EstimateDistance(float textHeight)
    {
        if (textHeight <= 0) return float.MaxValue;

        // Simple inverse relationship: distance = (reference * focal) / observed
        // This is based on pinhole camera model
        float distance = (ReferenceHeightAt1m * FocalLength) / textHeight;

        // Clamp to reasonable game distances (0.5m to 100m)
        return Math.Clamp(distance, 0.5f, 100f);
    }

    /// <summary>
    /// Check if text looks like a player name.
    /// </summary>
    private static bool IsLikelyPlayerName(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length < 2 || text.Length > 32) return false;

        // Player names typically:
        // - Start with letter or [
        // - Don't contain only numbers
        // - Don't look like game UI text

        var gameKeywords = new[] { "health", "ammo", "wave", "score", "press", "reload", "buy" };
        var lower = text.ToLower();

        if (gameKeywords.Any(k => lower.Contains(k))) return false;
        if (text.All(char.IsDigit)) return false;

        return char.IsLetter(text[0]) || text[0] == '[';
    }

    /// <summary>
    /// Check if text looks like game UI text.
    /// </summary>
    private static bool IsLikelyGameText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var gameKeywords = new[] {
            "health", "ammo", "wave", "score", "press", "reload", "buy",
            "zombie", "round", "points", "weapon", "barricade", "door"
        };

        var lower = text.ToLower();
        return gameKeywords.Any(k => lower.Contains(k));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine?.Dispose();
        _engine = null;
    }
}

/// <summary>
/// Result of text recognition.
/// </summary>
public sealed class TextRecognitionResult
{
    public DateTime Timestamp { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public float MeanConfidence { get; set; }
    public List<RecognizedText> Words { get; } = new();
    public string? Error { get; set; }

    /// <summary>Get all player names detected.</summary>
    public IEnumerable<RecognizedText> PlayerNames =>
        Words.Where(w => w.IsPlayerName);

    /// <summary>Get all game text detected.</summary>
    public IEnumerable<RecognizedText> GameText =>
        Words.Where(w => w.IsGameText);

    /// <summary>Get nearest text by estimated distance.</summary>
    public RecognizedText? NearestText =>
        Words.OrderBy(w => w.EstimatedDistance).FirstOrDefault();
}

/// <summary>
/// A single recognized text element.
/// </summary>
public sealed class RecognizedText
{
    /// <summary>The recognized text content.</summary>
    public string Text { get; init; } = "";

    /// <summary>Recognition confidence [0, 1].</summary>
    public float Confidence { get; init; }

    /// <summary>Bounding box in screen coordinates.</summary>
    public BoundingBox Box { get; set; }

    /// <summary>Font height in pixels.</summary>
    public float FontHeight { get; init; }

    /// <summary>Estimated distance in game units (meters).</summary>
    public float EstimatedDistance { get; init; }

    /// <summary>Center position on screen.</summary>
    public Vector2 ScreenPosition { get; set; }

    /// <summary>Whether this looks like a player name.</summary>
    public bool IsPlayerName { get; init; }

    /// <summary>Whether this looks like game UI text.</summary>
    public bool IsGameText { get; init; }

    /// <summary>Distance category.</summary>
    public DistanceCategory DistanceCategory =>
        EstimatedDistance switch
        {
            < 2f => DistanceCategory.VeryClose,
            < 5f => DistanceCategory.Close,
            < 15f => DistanceCategory.Medium,
            < 30f => DistanceCategory.Far,
            _ => DistanceCategory.VeryFar
        };

    public override string ToString() =>
        $"\"{Text}\" @ {EstimatedDistance:F1}m ({DistanceCategory})";
}

/// <summary>
/// Distance categories for easy classification.
/// </summary>
public enum DistanceCategory
{
    VeryClose,  // < 2m
    Close,      // 2-5m
    Medium,     // 5-15m
    Far,        // 15-30m
    VeryFar     // > 30m
}

/// <summary>
/// Helper to convert System.Drawing.Bitmap to Tesseract Pix.
/// </summary>
internal static class PixConverter
{
    public static Pix ToPix(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        return Pix.LoadFromMemory(stream.ToArray());
    }
}
