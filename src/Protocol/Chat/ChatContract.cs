using System.Text.Json.Serialization;

namespace CommunityToolkit.AI.Protocol.Chat;

/// <summary>Chat task contract. Owned by the Chat package and versioned independently.</summary>
internal static class ChatContract
{
    public const string TaskId = "chat";

    /// <summary>Max contract version this build speaks.</summary>
    public const string Version = "1.0";

    /// <summary>Streaming. Events are <see cref="ChatChunk"/>; the result is <see cref="ChatCompletionResult"/>.</summary>
    public const string Complete = "chat.complete";

    /// <summary>Drops any per-session state (the KV cache) held by the host for this connection.</summary>
    public const string Reset = "chat.reset";
}

internal sealed class ChatWireMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

internal sealed class ChatWireOptions
{
    [JsonPropertyName("maxOutputTokens")] public int? MaxOutputTokens { get; set; }
    [JsonPropertyName("temperature")] public float? Temperature { get; set; }
    [JsonPropertyName("topP")] public float? TopP { get; set; }
    [JsonPropertyName("topK")] public int? TopK { get; set; }
    [JsonPropertyName("seed")] public long? Seed { get; set; }
    [JsonPropertyName("stop")] public List<string>? StopSequences { get; set; }
}

internal sealed class ChatCompletionRequest
{
    [JsonPropertyName("messages")] public List<ChatWireMessage> Messages { get; set; } = new();
    [JsonPropertyName("options")] public ChatWireOptions? Options { get; set; }
}

internal sealed class ChatChunk
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

internal sealed class ChatCompletionResult
{
    /// <summary>stop, length or cancelled.</summary>
    [JsonPropertyName("finishReason")] public string FinishReason { get; set; } = "stop";
    [JsonPropertyName("inputTokens")] public int InputTokens { get; set; }
    [JsonPropertyName("outputTokens")] public int OutputTokens { get; set; }
    [JsonPropertyName("modelId")] public string? ModelId { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatChunk))]
[JsonSerializable(typeof(ChatCompletionResult))]
internal sealed partial class ChatJsonContext : JsonSerializerContext
{
}
