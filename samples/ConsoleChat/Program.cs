using CommunityToolkit.AI;
using CommunityToolkit.AI.Chat;
using Microsoft.Extensions.AI;

// Usage: ConsoleChat [model-id] [--prompt "text"] [--yes] [--dev]
//   --prompt  answer one prompt and exit (used by the CI smoke test)
//   --yes     accept the licence and the download without asking
//   --dev     enable dev mode (LOCALAI_DEV_ROOT_URL, LOCALAI_DEV_TRUST_KEY, LOCALAI_HOME, LOCALAI_CHANNEL)
string modelId = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "qwen-2.5-1.5b-instruct";
string? oneShot = args.SkipWhile(a => a != "--prompt").Skip(1).FirstOrDefault();
bool yes = args.Contains("--yes");
LocalAIRuntime.EnableDevMode = args.Contains("--dev");
LocalAIRuntime.ClientName = "ConsoleChat";

bool Confirm(string question)
{
    if (yes)
    {
        return true;
    }

    Console.Write($"{question} [y/N] ");
    return Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;
}

var model = ChatModel.FromCatalog(modelId, new ModelOptions
{
    LicenseAcceptance = (license, _) => ValueTask.FromResult(Confirm($"{license.ModelId} is licensed under {license.LicenseId} ({license.Url}). Accept?")),
    DownloadConsent = (request, _) => ValueTask.FromResult(Confirm($"Download {request.Bytes / (1024.0 * 1024):N0} MB for {request.ModelId}?")),
});

try
{
    Console.WriteLine($"{model.Id}: {await model.GetStatusAsync()}");
    await model.EnsureReadyAsync(new Progress<EnsureProgress>(p =>
        Console.Write($"\r{p.Stage,-8} {p.OverallFraction,6:P0}  {p.BytesDone / (1024.0 * 1024),8:N1} / {p.BytesTotal / (1024.0 * 1024):N1} MB   ")));
    Console.WriteLine();

    IChatClient client = await model.GetClientAsync();
    var history = new List<ChatMessage> { new(ChatRole.System, "You are a helpful assistant. Keep answers short.") };
    while (true)
    {
        string? prompt = oneShot;
        if (prompt is null)
        {
            Console.Write("> ");
            prompt = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                break;
            }
        }

        history.Add(new ChatMessage(ChatRole.User, prompt));
        string reply = "";
        await foreach (var update in client.GetStreamingResponseAsync(history, new ChatOptions { MaxOutputTokens = 256 }))
        {
            Console.Write(update.Text);
            reply += update.Text;
        }

        Console.WriteLine();
        history.Add(new ChatMessage(ChatRole.Assistant, reply));
        if (oneShot is not null)
        {
            return reply.Length > 0 ? 0 : 1;
        }
    }
}
catch (LocalAiException ex)
{
    Console.Error.WriteLine($"\n{ex.GetType().Name} ({ex.ErrorCode}): {ex.Message}");
    return 1;
}

return 0;
