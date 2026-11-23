using System.Text.Json.Serialization;

namespace CommunityToolkit.AI.Protocol.Ocr;

/// <summary>OCR task contract. Owned by the Ocr package and versioned independently.</summary>
internal static class OcrContract
{
    public const string TaskId = "ocr";
    public const string Version = "0.1";
    public const string Recognize = "ocr.recognize";
}

internal sealed class OcrRecognizeRequest
{
    /// <summary>Encoded image bytes (PNG, JPEG or BMP), base64. Moves to the shared-memory data plane in sprint 7.</summary>
    [JsonPropertyName("image")] public string Image { get; set; } = "";
    [JsonPropertyName("language")] public string? Language { get; set; }
}

internal sealed class OcrWireLine
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
    [JsonPropertyName("width")] public float Width { get; set; }
    [JsonPropertyName("height")] public float Height { get; set; }
    [JsonPropertyName("confidence")] public float Confidence { get; set; }
}

internal sealed class OcrRecognizeResult
{
    [JsonPropertyName("lines")] public List<OcrWireLine> Lines { get; set; } = new();
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OcrRecognizeRequest))]
[JsonSerializable(typeof(OcrRecognizeResult))]
internal sealed partial class OcrJsonContext : JsonSerializerContext
{
}
