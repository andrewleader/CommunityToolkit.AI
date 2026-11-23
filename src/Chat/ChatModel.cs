using CommunityToolkit.AI.Protocol.Chat;
using Microsoft.Extensions.AI;

namespace CommunityToolkit.AI.Chat;

/// <summary>A local chat model from the catalog.</summary>
/// <example>
/// <code>
/// var model = ChatModel.FromCatalog("qwen-2.5-1.5b-instruct");
/// await model.EnsureReadyAsync(progress);
/// IChatClient client = await model.GetClientAsync();
/// var response = await client.GetResponseAsync("Hello!");
/// </code>
/// </example>
public sealed class ChatModel : Model
{
    private ChatModel(string id)
        : base(id, ChatContract.TaskId)
    {
    }

    /// <summary>
    /// Returns a handle to a catalog model. Does no I/O. Whether the id is a chat model is checked by the runtime on first use,
    /// and a mismatch raises <see cref="ModelUnknownException"/>.
    /// </summary>
    public static ChatModel FromCatalog(string id) => new(id);

    /// <inheritdoc cref="FromCatalog(string)"/>
    public static ChatModel FromCatalog(string id, ModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ChatModel(id) { Options = options };
    }

    /// <summary>
    /// Finds or starts the shared model host and returns a session-bound <see cref="IChatClient"/>.
    /// Call <see cref="Model.EnsureReadyAsync"/> first.
    /// </summary>
    public async Task<IChatClient> GetClientAsync(ChatClientOptions? options = null, CancellationToken ct = default)
    {
        var connection = await ConnectToHostAsync(ChatContract.Version, ct).ConfigureAwait(false);
        return new LocalChatClient(connection, options ?? new ChatClientOptions());
    }
}

/// <summary>Defaults applied when a request's <see cref="ChatOptions"/> does not set them.</summary>
public sealed class ChatClientOptions
{
    public int? DefaultMaxOutputTokens { get; set; }

    public float? DefaultTemperature { get; set; }
}
