using System.Text.Json.Serialization;

namespace CommunityToolkit.AI.Protocol.SpeechToText;

/// <summary>Speech-to-text task contract. Owned by the SpeechToText package and versioned independently.</summary>
internal static class SpeechToTextContract
{
    public const string TaskId = "speech-to-text";
    public const string Version = "0.1";

    /// <summary>Streaming. Events are <see cref="TranscriptSegmentWire"/>; the result is <see cref="TranscribeResult"/>.</summary>
    public const string Transcribe = "stt.transcribe";
}

internal sealed class TranscribeRequest
{
    /// <summary>16 kHz mono 16-bit PCM WAV bytes, base64. Moves to the shared-memory data plane in sprint 8.</summary>
    [JsonPropertyName("audio")] public string Audio { get; set; } = "";
    [JsonPropertyName("language")] public string? Language { get; set; }
}

internal sealed class TranscriptSegmentWire
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("startMs")] public long StartMs { get; set; }
    [JsonPropertyName("endMs")] public long EndMs { get; set; }
}

internal sealed class TranscribeResult
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("language")] public string? Language { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TranscribeRequest))]
[JsonSerializable(typeof(TranscriptSegmentWire))]
[JsonSerializable(typeof(TranscribeResult))]
internal sealed partial class SpeechToTextJsonContext : JsonSerializerContext
{
}
