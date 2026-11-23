# CommunityToolkit.AI

Local AI models for any Windows app, packaged or unpackaged, through a small API and a shared, self-updating runtime.

```csharp
ChatModel model = ChatModel.FromCatalog("qwen-2.5-1.5b-instruct");
await model.EnsureReadyAsync(progress);            // installs the runtime and the model on first use
IChatClient client = await model.GetClientAsync(); // Microsoft.Extensions.AI
ChatResponse response = await client.GetResponseAsync("Hello!");
```

- The app-side packages are tiny (Core plus one task package stays under 2 MB) and contain no model code.
- Inference runs out-of-process in a shared host; a model crash never crashes the app.
- Apps that use the same model share one host process and one copy on disk.
- The runtime and models download on demand into per-user folders, verified against signed manifests.
- The runtime updates itself without app updates.

Docs: https://andrewleader.github.io/CommunityToolkit.AI/ — start with the
[console quick start](docs/quickstart-console.md). The design is in [LocalAI-Runtime-Spec.md](LocalAI-Runtime-Spec.md).

## Status

Preview. Chat works end to end on CPU with `qwen-2.5-1.5b-instruct`. The OCR and speech-to-text packages publish their
APIs and contracts; their runtime adapters, GPU/NPU execution providers through Windows ML, the host sandbox and
Authenticode signing are on the [roadmap](docs/architecture.md#not-yet-implemented).

## Repository

| Path | Contents |
|---|---|
| `src/Core`, `src/Chat`, `src/Ocr`, `src/SpeechToText` | Client NuGet packages |
| `src/Protocol` | Wire protocol, task contracts, trust (Ed25519 manifests, verified downloads) |
| `src/Broker`, `src/Host` | `broker.exe` and `host.exe` |
| `catalog/models` | Model catalog entries (files stay on Hugging Face, pinned by commit) |
| `tools` | `localai-tools` (keys, signing, runtime packing, catalog, static NuGet feed) and `dev-site.ps1` |
| `tests` | Protocol tests and end-to-end runtime tests |
| `samples` | Sample apps, compiled and smoke-tested in CI |
| `docs` | Documentation site (MkDocs) |

```powershell
dotnet build CommunityToolkit.AI.slnx
dotnet test tests/CommunityToolkit.AI.Protocol.Tests
dotnet test tests/CommunityToolkit.AI.Runtime.Tests
```

See [local development](docs/maintainers/local-dev.md) to run the sample against a locally signed site.
