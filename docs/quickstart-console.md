# Quick start: console app (unpackaged)

1. Add the preview feed (see [Preview NuGet feed](nuget-feed.md)) and the Chat package:

    ```powershell
    dotnet nuget add source https://andrewleader.github.io/CommunityToolkit.AI/nuget/v3/index.json -n CommunityToolkit-AI-Preview
    dotnet new console -n HelloLocalAI
    cd HelloLocalAI
    dotnet add package CommunityToolkit.AI.Chat --prerelease
    ```

2. Replace `Program.cs`. This is the repo's `samples/ConsoleChat` sample, which CI compiles and runs on every PR:

    ```csharp
    --8<-- "samples/ConsoleChat/Program.cs"
    ```

3. Run it. The first run downloads the runtime (tens of MB) and the model (about 1 GB), asking for licence acceptance
   and consent first. Later runs start in seconds.

    ```powershell
    dotnet run
    ```

What happened:

- `ChatModel.FromCatalog` created a handle. It did no I/O.
- `EnsureReadyAsync` installed the runtime into `%LocalAppData%\CommunityToolkit\LocalAI`, started the per-user broker,
  and downloaded and verified the model. See [How EnsureReadyAsync works](ensure-ready.md).
- `GetClientAsync` started (or reused) the shared model host and returned an `IChatClient` bound to your own session.
