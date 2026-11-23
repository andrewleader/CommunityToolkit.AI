using CommunityToolkit.AI.Protocol.Chat;

namespace CommunityToolkit.AI.Host.Adapters;

/// <summary>What the host knows about the model it serves.</summary>
internal sealed record ModelInfo(string Id, string Version, string Directory, string Ep, IReadOnlyDictionary<string, string> Parameters);

/// <summary>A task adapter. Adapters are allow-listed and ship inside the runtime; no downloaded code is executed.</summary>
internal interface ITaskAdapter : IDisposable
{
    string Task { get; }

    /// <summary>Creates per-connection state. Sessions are isolated: nothing is shared across apps.</summary>
    IDisposable? CreateSession();
}

/// <summary>Internal chat API that every supported chat contract version is translated into.</summary>
internal interface IChatAdapter : ITaskAdapter
{
    /// <summary>Generates a reply, calling <paramref name="onText"/> for each decoded piece. Runs on a dedicated thread.</summary>
    ChatCompletionResult Generate(IDisposable? session, ChatCompletionRequest request, Action<string> onText, CancellationToken ct);
}

internal static class AdapterRegistry
{
    public static ITaskAdapter? Create(string adapterId, ModelInfo model) => adapterId switch
    {
        EchoChatAdapter.Id => new EchoChatAdapter(model),
        GenAIChatAdapter.Id => new GenAIChatAdapter(model),
        _ => null,
    };
}
