using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CommunityToolkit.AI.Protocol.Trust;

namespace CommunityToolkit.AI.Tools;

internal static class FeedCommands
{
    public static readonly string[] PackageIds = ["CommunityToolkit.AI.Core", "CommunityToolkit.AI.Chat", "CommunityToolkit.AI.SpeechToText", "CommunityToolkit.AI.Ocr"];

    /// <summary>Size budgets from spec 4.1: Core at most 1 MB, each task package at most 500 KB.</summary>
    private static long Budget(string id) => id == "CommunityToolkit.AI.Core" ? 1024 * 1024 : 500 * 1024;

    public static async Task<int> RunAsync(Args a)
    {
        if (a.Positional.FirstOrDefault() != "build")
        {
            throw new ToolException("usage: feed build --packages <dir> --base-url <url> --out <dir>");
        }

        string baseUrl = a.Required("base-url").TrimEnd('/') + "/";
        string output = a.Required("out");
        string packagesDir = a.Required("packages");
        var packages = Directory.EnumerateFiles(packagesDir, "*.nupkg").Where(p => !p.EndsWith(".symbols.nupkg")).Select(ReadPackage).ToList();

        // Static feeds have no push or delete, so every previously published version is carried forward.
        if (a.Optional("previous-base-url") is { } previous)
        {
            packages.AddRange(await FetchPreviousAsync(previous.TrimEnd('/') + "/", packages, packagesDir));
        }

        string flat = Path.Combine(output, "flatcontainer");
        string registration = Path.Combine(output, "registration");
        foreach (var group in packages.GroupBy(p => p.Id.ToLowerInvariant()))
        {
            var versions = group.GroupBy(p => p.Version.ToLowerInvariant()).Select(g => g.First()).OrderBy(p => p.Version, Comparer<string>.Create(SemVer.Compare)).ToList();
            foreach (var package in versions)
            {
                string dir = Path.Combine(flat, group.Key, package.Version.ToLowerInvariant());
                Directory.CreateDirectory(dir);
                File.Copy(package.Path, Path.Combine(dir, $"{group.Key}.{package.Version.ToLowerInvariant()}.nupkg"), overwrite: true);
                File.WriteAllText(Path.Combine(dir, $"{group.Key}.nuspec"), package.Nuspec);
            }

            WriteJson(Path.Combine(flat, group.Key, "index.json"), new JsonObject { ["versions"] = new JsonArray(versions.Select(v => (JsonNode)v.Version.ToLowerInvariant()).ToArray()) });
            WriteRegistration(Path.Combine(registration, group.Key), baseUrl, group.Key, versions);
            Console.WriteLine($"{versions[0].Id}: {string.Join(", ", versions.Select(v => v.Version))}");
        }

        WriteJson(Path.Combine(output, "index.json"), new JsonObject
        {
            ["version"] = "3.0.0",
            ["resources"] = new JsonArray(
                Resource(baseUrl + "flatcontainer/", "PackageBaseAddress/3.0.0"),
                Resource(baseUrl + "registration/", "RegistrationsBaseUrl"),
                Resource(baseUrl + "registration/", "RegistrationsBaseUrl/3.0.0-rc"),
                Resource(baseUrl + "registration/", "RegistrationsBaseUrl/3.4.0"),
                Resource(baseUrl + "registration/", "RegistrationsBaseUrl/3.6.0")),
        });
        Console.WriteLine($"Static NuGet feed written to {output} for {baseUrl}index.json");
        return 0;
    }

    public static int SizeGate(Args a)
    {
        int failures = 0;
        foreach (var path in Directory.EnumerateFiles(a.Required("packages"), "*.nupkg").Where(p => !p.EndsWith(".symbols.nupkg")))
        {
            var package = ReadPackage(path);
            long size = new FileInfo(path).Length;
            long budget = Budget(package.Id);
            bool ok = size <= budget;
            failures += ok ? 0 : 1;
            Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {package.Id} {package.Version}: {size:N0} bytes (budget {budget:N0})");
        }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Lists the packages a change affects. Core, the core protocol, trust code and build files affect everything;
    /// a task folder affects only its package. Writes packages= and runtime= to $GITHUB_OUTPUT when set.
    /// </summary>
    public static int Affected(Args a)
    {
        string baseRef = a.Required("base");
        var psi = new ProcessStartInfo("git", $"diff --name-only {baseRef}...HEAD") { RedirectStandardOutput = true, UseShellExecute = false };
        using var git = Process.Start(psi)!;
        var files = git.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        git.WaitForExit();

        var affected = new SortedSet<string>(StringComparer.Ordinal);
        bool runtime = false;
        foreach (var file in files.Select(f => f.Replace('\\', '/')))
        {
            if (file.StartsWith("src/Core/") || file.StartsWith("src/Protocol/Core/") || file.StartsWith("src/Protocol/Trust/") || file.StartsWith("Directory.Build.") || file == "nuget.config")
            {
                affected.UnionWith(PackageIds);
            }

            foreach (var (folder, id) in new[] { ("Chat", "CommunityToolkit.AI.Chat"), ("Ocr", "CommunityToolkit.AI.Ocr"), ("SpeechToText", "CommunityToolkit.AI.SpeechToText") })
            {
                if (file.StartsWith($"src/{folder}/") || file.StartsWith($"src/Protocol/{folder}/"))
                {
                    affected.Add(id);
                }
            }

            runtime |= file.StartsWith("src/Broker/") || file.StartsWith("src/Host/") || file.StartsWith("src/Protocol/") || file.StartsWith("Directory.Build.");
        }

        string packages = string.Join(",", affected);
        Console.WriteLine($"packages={packages}");
        Console.WriteLine($"runtime={runtime.ToString().ToLowerInvariant()}");
        if (Environment.GetEnvironmentVariable("GITHUB_OUTPUT") is { Length: > 0 } outputFile)
        {
            File.AppendAllText(outputFile, $"packages={packages}\nruntime={runtime.ToString().ToLowerInvariant()}\n");
        }

        return 0;
    }

    private sealed record PackageInfo(string Path, string Id, string Version, string Nuspec, XDocument Document);

    private static PackageInfo ReadPackage(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'))
            ?? throw new ToolException($"{path} has no nuspec.");
        using var reader = new StreamReader(entry.Open());
        string nuspec = reader.ReadToEnd();
        var doc = XDocument.Parse(nuspec);
        var metadata = doc.Root!.Elements().First(e => e.Name.LocalName == "metadata");
        string Value(string name) => metadata.Elements().First(e => e.Name.LocalName == name).Value;
        return new PackageInfo(path, Value("id"), Value("version"), nuspec, doc);
    }

    private static async Task<List<PackageInfo>> FetchPreviousAsync(string previousBase, List<PackageInfo> current, string downloadDir)
    {
        using var http = new HttpClient();
        var result = new List<PackageInfo>();
        foreach (var id in PackageIds.Concat(current.Select(p => p.Id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string lower = id.ToLowerInvariant();
            using var response = await http.GetAsync($"{previousBase}flatcontainer/{lower}/index.json");
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            var versions = JsonNode.Parse(await response.Content.ReadAsStringAsync())?["versions"]?.AsArray().Select(v => v!.GetValue<string>()) ?? [];
            foreach (var version in versions)
            {
                if (current.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && p.Version.Equals(version, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string target = Path.Combine(downloadDir, $"{lower}.{version}.nupkg");
                await File.WriteAllBytesAsync(target, await http.GetByteArrayAsync($"{previousBase}flatcontainer/{lower}/{version}/{lower}.{version}.nupkg"));
                result.Add(ReadPackage(target));
            }
        }

        Console.WriteLine($"Carried forward {result.Count} previously published package versions from {previousBase}.");
        return result;
    }

    private static void WriteRegistration(string dir, string baseUrl, string lowerId, List<PackageInfo> versions)
    {
        string indexUrl = $"{baseUrl}registration/{lowerId}/index.json";
        var items = new JsonArray();
        foreach (var package in versions)
        {
            string lowerVersion = package.Version.ToLowerInvariant();
            string leafUrl = $"{baseUrl}registration/{lowerId}/{lowerVersion}.json";
            string content = $"{baseUrl}flatcontainer/{lowerId}/{lowerVersion}/{lowerId}.{lowerVersion}.nupkg";
            var metadata = package.Document.Root!.Elements().First(e => e.Name.LocalName == "metadata");
            string? Meta(string name) => metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
            var groups = new JsonArray();
            foreach (var group in metadata.Descendants().Where(e => e.Name.LocalName == "group"))
            {
                groups.Add(new JsonObject
                {
                    ["targetFramework"] = group.Attribute("targetFramework")?.Value,
                    ["dependencies"] = new JsonArray(group.Elements().Select(d => (JsonNode)new JsonObject
                    {
                        ["id"] = d.Attribute("id")?.Value,
                        ["range"] = d.Attribute("version")?.Value,
                    }).ToArray()),
                });
            }

            var leaf = new JsonObject
            {
                ["@id"] = leafUrl,
                ["catalogEntry"] = new JsonObject
                {
                    ["@id"] = leafUrl,
                    ["id"] = package.Id,
                    ["version"] = package.Version,
                    ["description"] = Meta("description"),
                    ["authors"] = Meta("authors"),
                    ["licenseExpression"] = Meta("license"),
                    ["projectUrl"] = Meta("projectUrl"),
                    ["tags"] = Meta("tags"),
                    ["listed"] = true,
                    ["packageContent"] = content,
                    ["dependencyGroups"] = groups,
                },
                ["packageContent"] = content,
                ["registration"] = indexUrl,
            };
            WriteJson(Path.Combine(dir, $"{lowerVersion}.json"), leaf.DeepClone());
            items.Add(leaf);
        }

        WriteJson(Path.Combine(dir, "index.json"), new JsonObject
        {
            ["@id"] = indexUrl,
            ["count"] = 1,
            ["items"] = new JsonArray(new JsonObject
            {
                ["@id"] = $"{indexUrl}#page/{versions[0].Version.ToLowerInvariant()}/{versions[^1].Version.ToLowerInvariant()}",
                ["count"] = versions.Count,
                ["lower"] = versions[0].Version.ToLowerInvariant(),
                ["upper"] = versions[^1].Version.ToLowerInvariant(),
                ["items"] = items,
            }),
        });
    }

    private static JsonObject Resource(string id, string type) => new() { ["@id"] = id, ["@type"] = type };

    private static void WriteJson(string path, JsonNode node)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
