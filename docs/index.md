# Local AI for Windows apps

CommunityToolkit.AI gives any Windows app, packaged or unpackaged, a small API for local models: chat today, with OCR and
speech to text on the way.

```csharp
using CommunityToolkit.AI.Chat;
using Microsoft.Extensions.AI;

ChatModel model = ChatModel.FromCatalog("qwen-2.5-1.5b-instruct"); // a handle, no I/O
await model.EnsureReadyAsync(progress);                            // runtime + model, verified
IChatClient client = await model.GetClientAsync();                 // Microsoft.Extensions.AI
ChatResponse response = await client.GetResponseAsync("Hello!");
Console.WriteLine(response.Text);
```

## Why it is built this way

- **Tiny app footprint.** Your app references a NuGet package of well under 2 MB. It contains no model code, no ONNX
  Runtime and no Windows ML.
- **Out-of-process inference.** Models run in a separate host process. A model crash never crashes your app; the next
  call restarts the host.
- **Shared by every app.** Apps that use the same model share one host process and one copy of the model on disk.
- **On demand.** The runtime and models download the first time an app needs them, into per-user folders, with no
  elevation.
- **Self-updating.** The runtime updates itself without app updates. Your app only speaks a versioned wire contract.

## Packages

| Package | What it gives you |
|---|---|
| `CommunityToolkit.AI.Chat` | `ChatModel` and an `IChatClient` |
| `CommunityToolkit.AI.Ocr` | `OcrModel` and `OcrClient` (preview API; the runtime adapter is in progress) |
| `CommunityToolkit.AI.SpeechToText` | `SpeechToTextModel` and `SpeechToTextClient` (preview API; the runtime adapter is in progress) |
| `CommunityToolkit.AI.Core` | Shared by the task packages: `Model`, status, progress, errors and options |

Each task package is versioned on its own, so you can move to a new major version of one task without touching another.

Next: [build a console app](quickstart-console.md).
