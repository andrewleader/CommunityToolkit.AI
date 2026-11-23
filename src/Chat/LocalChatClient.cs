using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CommunityToolkit.AI.Protocol.Chat;
using Microsoft.Extensions.AI;

namespace CommunityToolkit.AI.Chat;

/// <summary>An <see cref="IChatClient"/> that proxies to the shared model host. Each instance is its own isolated session.</summary>
internal sealed class LocalChatClient : IChatClient
{
    private readonly ModelHostConnection _connection;
    private readonly ChatClientOptions _defaults;
    private readonly ChatClientMetadata _metadata;

    public LocalChatClient(ModelHostConnection connection, ChatClientOptions defaults)
    {
        _connection = connection;
        _defaults = defaults;
        _metadata = new ChatClientMetadata("CommunityToolkit.AI", providerUri: null, defaultModelId: connection.ModelId);
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        ChatFinishReason? finishReason = null;
        UsageDetails? usage = null;
        string? responseId = null;
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            responseId ??= update.ResponseId;
            finishReason = update.FinishReason ?? finishReason;
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent t:
                        text.Append(t.Text);
                        break;
                    case UsageContent u:
                        usage = u.Details;
                        break;
                }
            }
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text.ToString()))
        {
            ResponseId = responseId,
            ModelId = _connection.ModelId,
            FinishReason = finishReason,
            Usage = usage,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var request = new ChatCompletionRequest
        {
            Messages = messages.Select(m => new ChatWireMessage { Role = m.Role.Value, Text = m.Text }).ToList(),
            Options = new ChatWireOptions
            {
                MaxOutputTokens = options?.MaxOutputTokens ?? _defaults.DefaultMaxOutputTokens,
                Temperature = options?.Temperature ?? _defaults.DefaultTemperature,
                TopP = options?.TopP,
                TopK = options?.TopK,
                Seed = options?.Seed,
                StopSequences = options?.StopSequences?.ToList(),
            },
        };

        if (options?.Instructions is { Length: > 0 } instructions)
        {
            request.Messages.Insert(0, new ChatWireMessage { Role = ChatRole.System.Value, Text = instructions });
        }

        string responseId = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.SerializeToElement(request, ChatJsonContext.Default.ChatCompletionRequest);
        await using var stream = await _connection.StreamAsync(ChatContract.Complete, payload, cancellationToken).ConfigureAwait(false);
        await foreach (var evt in stream.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
        {
            var chunk = evt.Deserialize(ChatJsonContext.Default.ChatChunk);
            if (chunk is { Text.Length: > 0 })
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk.Text)
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                    ModelId = _connection.ModelId,
                };
            }
        }

        var result = (await stream.GetResultAsync(cancellationToken).ConfigureAwait(false))?.Deserialize(ChatJsonContext.Default.ChatCompletionResult);
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            ResponseId = responseId,
            MessageId = responseId,
            ModelId = _connection.ModelId,
            FinishReason = result?.FinishReason switch
            {
                "length" => ChatFinishReason.Length,
                "stop" or null => ChatFinishReason.Stop,
                var other => new ChatFinishReason(other),
            },
            Contents = [new UsageContent(new UsageDetails { InputTokenCount = result?.InputTokens, OutputTokenCount = result?.OutputTokens })],
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return _metadata;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose() => _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
