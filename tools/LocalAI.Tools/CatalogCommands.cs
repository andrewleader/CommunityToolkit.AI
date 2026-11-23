using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.AI.Protocol.Trust;

namespace CommunityToolkit.AI.Tools;

/// <summary>Catalog tooling (spec 10.1). Models stay on Hugging Face; entries pin a commit and record every file's size and SHA-256.</summary>
internal static partial class CatalogCommands
{
    private const string HfBase = "https://huggingface.co";
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, TypeInfoResolver = TrustJsonContext.Default };

    public static async Task<int> RunAsync(Args a)
    {
        string sub = a.Positional.FirstOrDefault() ?? throw new ToolException("catalog needs a subcommand: add, build, check or docs.");
        return sub switch
        {
            "add" => await AddAsync(a),
            "build" => await BuildAsync(a),
            "check" => await CheckAsync(a),
            "docs" => Docs(a),
            _ => throw new ToolException($"Unknown catalog subcommand '{sub}'."),
        };
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 }) { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CommunityToolkit.AI-catalog-tools");
        if (Environment.GetEnvironmentVariable("HF_TOKEN") is { Length: > 0 } token)
        {
            // Only used for API rate limits in CI. Entries must still be anonymously downloadable (checked separately).
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return http;
    }

    /// <summary>catalog add &lt;hf-repo&gt; &lt;revision&gt;: resolves the commit, lists files with sizes and hashes, writes the entry.</summary>
    private static async Task<int> AddAsync(Args a)
    {
        if (a.Positional.Count < 3)
        {
            throw new ToolException("usage: catalog add <hf-repo> <revision> --id <id> ...");
        }

        string repo = a.Positional[1];
        string revision = a.Positional[2];
        using var http = CreateHttp();
        var info = await GetRepoInfoAsync(http, repo, revision);
        if (info.Gated || info.Private)
        {
            throw new ToolException($"{repo} is gated or private. Only anonymously downloadable repos are allowed in v1.");
        }

        string include = (a.Optional("include") ?? "").TrimStart('/');
        var files = new List<CatalogFile>();
        foreach (var file in await ListFilesAsync(http, repo, info.Sha))
        {
            if (!file.Path.StartsWith(include, StringComparison.Ordinal) || file.Path.EndsWith(".gitattributes") || file.Path.EndsWith("README.md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string sha256 = file.LfsSha256 ?? await HashRemoteAsync(http, FileUrl(repo, info.Sha, file.Path));
            files.Add(new CatalogFile
            {
                Path = include.Length == 0 || include.EndsWith('/') ? file.Path[include.Length..] : file.Path.Split('/')[^1],
                Url = FileUrl(repo, info.Sha, file.Path),
                Size = file.Size,
                Sha256 = sha256,
            });
            Console.WriteLine($"  {file.Path} {file.Size:N0} {sha256[..12]}{(file.LfsSha256 is null ? " (hashed)" : "")}");
        }

        if (files.Count == 0)
        {
            throw new ToolException($"No files matched '{include}' in {repo}@{info.Sha}.");
        }

        var parameters = a.All("param").Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");
        var entry = new CatalogModel
        {
            Id = a.Required("id"),
            Task = a.Required("task"),
            Version = a.Optional("version") ?? "1.0.0",
            DisplayName = a.Optional("display-name"),
            Description = a.Optional("description"),
            License = new CatalogLicense { Id = a.Required("license-id"), Url = a.Optional("license-url") ?? $"{HfBase}/{repo}", RequiresAcceptance = !a.Flag("no-license-acceptance") },
            MinRuntimeVersion = a.Optional("min-runtime-version"),
            Hardware = new CatalogHardware { MinMemoryMB = a.Int("min-memory-mb", 0), Eps = a.List("eps") is { Count: > 0 } eps ? eps : ["cpu"] },
            Adapter = new CatalogAdapter { Id = a.Required("adapter"), Parameters = parameters },
            Hidden = a.Flag("hidden"),
            HfRepo = repo,
            HfRevision = info.Sha,
            Files = files,
        };

        string output = a.Optional("out") ?? Path.Combine("catalog", "models", entry.Id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(entry, s_indented) + "\n");
        Console.WriteLine($"Wrote {output}: {entry.Id} from {repo}@{info.Sha[..12]}, {files.Count} files, {entry.TotalSize:N0} bytes.");
        return 0;
    }

    /// <summary>Merges per-model entries (optionally over a production catalog) into a catalog payload ready to sign.</summary>
    private static async Task<int> BuildAsync(Args a)
    {
        var models = new Dictionary<string, CatalogModel>(StringComparer.Ordinal);
        if (a.Optional("overlay") is { } overlay)
        {
            byte[] signed = overlay.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? await CreateHttp().GetByteArrayAsync(overlay)
                : await File.ReadAllBytesAsync(overlay);
            var document = TrustStore.Parse(signed);
            var production = JsonSerializer.Deserialize(Convert.FromBase64String(document.Payload), TrustJsonContext.Default.CatalogDocument)!;
            foreach (var model in production.Models)
            {
                models[model.Id] = model;
            }

            Console.WriteLine($"Overlaying {models.Count} models from {overlay}.");
        }

        foreach (var model in LoadModels(a.Required("models")))
        {
            models[model.Id] = model;
        }

        var catalog = new CatalogDocument
        {
            Sequence = a.Long("sequence", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            Expires = DateTimeOffset.UtcNow.AddDays(a.Int("expires-days", 30)),
            Models = models.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList(),
        };
        File.WriteAllText(a.Required("out"), JsonSerializer.Serialize(catalog, TrustJsonContext.Default.CatalogDocument));
        Console.WriteLine($"Catalog payload: {catalog.Models.Count} models, sequence {catalog.Sequence}.");
        return 0;
    }

    /// <summary>
    /// CI checks: revision-pinned URLs, recorded licence, public and ungated repo, sizes and hashes matching Hugging Face metadata.
    /// --reachability checks every URL anonymously; --full-hash downloads and hashes every file.
    /// </summary>
    private static async Task<int> CheckAsync(Args a)
    {
        var changed = a.List("changed").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var http = CreateHttp();
        using var anonymous = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(30) };
        anonymous.DefaultRequestHeaders.UserAgent.ParseAdd("CommunityToolkit.AI-catalog-check");
        int failures = 0;
        void Fail(string id, string message)
        {
            failures++;
            Console.WriteLine($"FAIL {id}: {message}");
        }

        foreach (var (file, model) in LoadModelFiles(a.Required("models")))
        {
            if (changed.Count > 0 && !changed.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            string id = model.Id;
            if (string.IsNullOrWhiteSpace(model.License.Id))
            {
                Fail(id, "no licence id");
            }

            bool isLocalTestModel = model.Adapter.Id == "test-echo" && model.HfRepo is null;
            if (isLocalTestModel)
            {
                Console.WriteLine($"SKIP {id}: local test model");
                continue;
            }

            if (model.HfRepo is null || model.HfRevision is null || !CommitSha().IsMatch(model.HfRevision))
            {
                Fail(id, "hfRepo and a 40-character hfRevision commit sha are required");
                continue;
            }

            foreach (var f in model.Files)
            {
                string expectedPrefix = $"{HfBase}/{model.HfRepo}/resolve/{model.HfRevision}/";
                if (!f.Url.StartsWith(expectedPrefix, StringComparison.Ordinal))
                {
                    Fail(id, $"{f.Path}: URL must start with {expectedPrefix} (revision-pinned, never main)");
                }

                if (f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit) || f.Size <= 0)
                {
                    Fail(id, $"{f.Path}: size and a SHA-256 are required");
                }
            }

            try
            {
                var info = await GetRepoInfoAsync(http, model.HfRepo, model.HfRevision);
                if (info.Gated || info.Private)
                {
                    Fail(id, "the repo is gated or private");
                }

                var remote = (await ListFilesAsync(http, model.HfRepo, model.HfRevision)).ToDictionary(r => r.Path, StringComparer.Ordinal);
                foreach (var f in model.Files)
                {
                    string remotePath = f.Url[$"{HfBase}/{model.HfRepo}/resolve/{model.HfRevision}/".Length..];
                    if (!remote.TryGetValue(remotePath, out var r))
                    {
                        Fail(id, $"{remotePath} is not in {model.HfRepo}@{model.HfRevision}");
                        continue;
                    }

                    if (r.Size != f.Size)
                    {
                        Fail(id, $"{remotePath}: size {f.Size} does not match Hugging Face ({r.Size})");
                    }

                    if (r.LfsSha256 is { } lfs && !lfs.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        Fail(id, $"{remotePath}: SHA-256 does not match the Hugging Face LFS metadata");
                    }

                    if (a.Flag("reachability") || a.Flag("full-hash"))
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Head, f.Url);
                        using var response = await anonymous.SendAsync(request);
                        if (!response.IsSuccessStatusCode)
                        {
                            Fail(id, $"{remotePath}: anonymous download returned {(int)response.StatusCode}");
                        }
                    }

                    if (a.Flag("full-hash") || (r.LfsSha256 is null && f.Size < 64 * 1024 * 1024))
                    {
                        string actual = await HashRemoteAsync(anonymous, f.Url);
                        if (!actual.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            Fail(id, $"{remotePath}: downloaded SHA-256 {actual} does not match the catalog");
                        }
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                Fail(id, $"Hugging Face request failed: {ex.Message}");
            }

            Console.WriteLine($"{(failures == 0 ? "OK  " : "....")} {id} ({model.HfRepo}@{model.HfRevision[..12]})");
        }

        Console.WriteLine(failures == 0 ? "All catalog checks passed." : $"{failures} catalog check(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Generates the docs catalog browser page from the catalog sources.</summary>
    private static int Docs(Args a)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Model catalog").AppendLine();
        sb.AppendLine("Generated from `catalog/models` by `localai-tools catalog docs`. Do not edit by hand.").AppendLine();
        sb.AppendLine("Model files are downloaded from Hugging Face at a pinned commit and verified against the SHA-256 in the signed catalog.").AppendLine();
        foreach (var group in LoadModels(a.Required("models")).Where(m => !m.Hidden).GroupBy(m => m.Task).OrderBy(g => g.Key))
        {
            sb.AppendLine($"## {group.Key}").AppendLine();
            sb.AppendLine("| Id | Name | Download | Licence | Devices | Source |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var m in group.OrderBy(m => m.Id))
            {
                string source = m.HfRepo is null ? "test only" : $"[{m.HfRepo}](https://huggingface.co/{m.HfRepo}/tree/{m.HfRevision})";
                string license = m.License.Url is { } url ? $"[{m.License.Id}]({url})" : m.License.Id;
                sb.AppendLine($"| `{m.Id}` | {m.DisplayName ?? m.Id} | {FormatSize(m.TotalSize)} | {license} | {string.Join(", ", m.Hardware.Eps)} | {source} |");
            }

            sb.AppendLine();
        }

        File.WriteAllText(a.Required("out"), sb.ToString());
        Console.WriteLine($"Wrote {a.Required("out")}.");
        return 0;
    }

    public static IEnumerable<CatalogModel> LoadModels(string dir) => LoadModelFiles(dir).Select(f => f.Model);

    private static IEnumerable<(string File, CatalogModel Model)> LoadModelFiles(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            var model = JsonSerializer.Deserialize(File.ReadAllBytes(file), TrustJsonContext.Default.CatalogModel) ?? throw new ToolException($"{file} is empty.");
            if (Path.GetFileNameWithoutExtension(file) != model.Id)
            {
                throw new ToolException($"{file} must be named after its id '{model.Id}'.");
            }

            yield return (file, model);
        }
    }

    private static string FileUrl(string repo, string sha, string path) =>
        $"{HfBase}/{repo}/resolve/{sha}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    private sealed record RepoInfo(string Sha, bool Gated, bool Private);

    private sealed record RemoteFile(string Path, long Size, string? LfsSha256);

    private static async Task<RepoInfo> GetRepoInfoAsync(HttpClient http, string repo, string revision)
    {
        using var response = await http.GetAsync($"{HfBase}/api/models/{repo}/revision/{Uri.EscapeDataString(revision)}");
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
        {
            throw new ToolException($"{repo}@{revision} was not found (or is private).");
        }

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        bool gated = root.TryGetProperty("gated", out var g) && g.ValueKind != JsonValueKind.False;
        bool isPrivate = root.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True;
        return new RepoInfo(root.GetProperty("sha").GetString()!, gated, isPrivate);
    }

    private static async Task<List<RemoteFile>> ListFilesAsync(HttpClient http, string repo, string sha)
    {
        var files = new List<RemoteFile>();
        string? url = $"{HfBase}/api/models/{repo}/tree/{sha}?recursive=true";
        while (url is not null)
        {
            using var response = await http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.GetProperty("type").GetString() != "file")
                {
                    continue;
                }

                string? lfs = item.TryGetProperty("lfs", out var l) && l.TryGetProperty("oid", out var oid) ? oid.GetString() : null;
                files.Add(new RemoteFile(item.GetProperty("path").GetString()!, item.GetProperty("size").GetInt64(), lfs));
            }

            // The tree API paginates through the Link header.
            url = response.Headers.TryGetValues("Link", out var links) ? NextLink().Match(string.Join(",", links)) is { Success: true } m ? m.Groups[1].Value : null : null;
        }

        return files;
    }

    private static async Task<string> HashRemoteAsync(HttpClient http, string url)
    {
        await using var stream = await http.GetStreamAsync(url);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitSha();

    [GeneratedRegex("<([^>]+)>;\\s*rel=\"next\"")]
    private static partial Regex NextLink();
}
