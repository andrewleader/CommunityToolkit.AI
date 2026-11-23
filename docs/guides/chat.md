# Chat

`CommunityToolkit.AI.Chat` exposes local chat models through `Microsoft.Extensions.AI`'s `IChatClient`, so code written
against `IChatClient` (including middleware such as logging or caching) works unchanged.

## Get a client

```csharp
var model = ChatModel.FromCatalog("qwen-2.5-1.5b-instruct", new ModelOptions
{
    Device = Device.Auto,                       // a preference; the runtime falls back
    AcquisitionPolicy = AcquisitionPolicy.Prompt,
    LicenseAcceptance = (license, ct) => AskUserAsync(license, ct),
    DownloadConsent = (request, ct) => AskUserAsync($"Download {request.Bytes / 1_000_000} MB?", ct),
});

await model.EnsureReadyAsync(progress);
IChatClient client = await model.GetClientAsync(new ChatClientOptions { DefaultMaxOutputTokens = 512 });
```

## Single response or streaming

```csharp
ChatResponse response = await client.GetResponseAsync("Summarize this in one line: ...");
Console.WriteLine(response.Text);
Console.WriteLine(response.Usage?.OutputTokenCount);

await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(history))
{
    Console.Write(update.Text);
}
```

## Options

| `ChatOptions` | Supported |
|---|---|
| `MaxOutputTokens` | Yes. `FinishReason` is `Length` when the limit is hit. |
| `Temperature`, `TopP`, `TopK`, `Seed` | Yes. Without them, decoding is greedy. |
| `StopSequences` | Yes, up to 16. |
| `Instructions` | Yes, sent as a system message. |
| Tools / function calling | Not yet. |

## Sessions and the KV cache

Each `IChatClient` is its own session in the shared host, and nothing is shared across apps. Within a session, the host
keeps the model's KV cache between turns: when you send the conversation again with a new user message, only the new
tokens are processed. Up to four sessions per model keep a cache; older ones are rebuilt on their next turn.

## Cancellation and crashes

Cancelling the token stops generation in the host. If the host process crashes, in-flight calls raise
`HostCrashedException`, and the next call on the same client restarts the host automatically.

## Migration guides

`CommunityToolkit.AI.Chat` 1.x speaks chat contract 1.x. When a 2.0 ships, its migration guide will be listed here, and the
runtime keeps serving 1.x for 12 months after that.
