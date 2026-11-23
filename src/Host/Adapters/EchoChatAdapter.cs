using CommunityToolkit.AI.Protocol.Chat;

namespace CommunityToolkit.AI.Host.Adapters;

/// <summary>
/// The tiny test model: replies "Echo: &lt;last user message&gt;" one word at a time. Used by the smoke tests so the whole
/// path (catalog, download, verification, host, streaming) runs without a multi-GB download.
/// </summary>
internal sealed class EchoChatAdapter : IChatAdapter
{
    public const string Id = "test-echo";
    private readonly int _delayMs;
    private readonly bool _allowCrash;

    public EchoChatAdapter(ModelInfo model)
    {
        // The model files must exist; their content is irrelevant to this adapter.
        if (!Directory.EnumerateFiles(model.Directory).Any(f => !f.EndsWith("model.json", StringComparison.OrdinalIgnoreCase)))
        {
            throw new FileNotFoundException("The test model has no files.");
        }

        _delayMs = model.Parameters.TryGetValue("tokenDelayMs", out var d) && int.TryParse(d, out int delay) ? delay : 0;
        _allowCrash = model.Parameters.TryGetValue("allowCrash", out var c) && c == "true";
    }

    public string Task => ChatContract.TaskId;

    public IDisposable? CreateSession() => new EchoSession();

    public ChatCompletionResult Generate(IDisposable? session, ChatCompletionRequest request, Action<string> onText, CancellationToken ct)
    {
        string input = request.Messages.LastOrDefault(m => m.Role == "user")?.Text ?? "";
        if (_allowCrash && input == "__crash__")
        {
            Environment.FailFast("Test crash requested.");
        }

        var state = (EchoSession?)session;
        if (state is not null)
        {
            state.Turns++;
        }

        string[] words = ("Echo: " + input).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int limit = request.Options?.MaxOutputTokens ?? int.MaxValue;
        int produced = 0;
        foreach (var word in words)
        {
            ct.ThrowIfCancellationRequested();
            if (produced == limit)
            {
                return new ChatCompletionResult { FinishReason = "length", InputTokens = words.Length - 1, OutputTokens = produced };
            }

            onText(produced == 0 ? word : " " + word);
            produced++;
            if (_delayMs > 0)
            {
                Thread.Sleep(_delayMs);
            }
        }

        return new ChatCompletionResult { FinishReason = "stop", InputTokens = words.Length - 1, OutputTokens = produced };
    }

    public void Dispose()
    {
    }

    private sealed class EchoSession : IDisposable
    {
        public int Turns { get; set; }

        public void Dispose()
        {
        }
    }
}
