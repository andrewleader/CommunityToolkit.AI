# Preview NuGet feed

The packages are published to a static NuGet v3 feed on GitHub Pages until they move to nuget.org.

```powershell
dotnet nuget add source https://andrewleader.github.io/CommunityToolkit.AI/nuget/v3/index.json -n CommunityToolkit-AI-Preview
dotnet add package CommunityToolkit.AI.Chat --prerelease
```

- Versions are prerelease (for example `0.1.0-preview.123`), so opt in to prerelease versions.
- Each package has its own version history. A change that only touches OCR bumps only `CommunityToolkit.AI.Ocr`.
- Every published version stays on the feed, so apps pinned to an older version keep restoring.
- The feed has no search, so browse versions at `.../nuget/v3/flatcontainer/<package-id-lowercase>/index.json`.
- GitHub Pages caches for about 10 minutes, so a new version can take a few minutes to appear.

## Pull request builds

Every pull request in this repo publishes its affected packages to its own feed,
`https://andrewleader.github.io/CommunityToolkit.AI/pr/<number>/nuget/v3/index.json`, with versions like
`0.1.0-pr42.7`. Unchanged packages come from the main feed, so add both sources. The PR's bot comment has the exact
commands.
