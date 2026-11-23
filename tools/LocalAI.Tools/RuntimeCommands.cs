using System.IO.Compression;
using System.Text.Json;
using CommunityToolkit.AI.Protocol;
using CommunityToolkit.AI.Protocol.Trust;

namespace CommunityToolkit.AI.Tools;

internal static class RuntimeCommands
{
    public static readonly string[] DefaultDownloadHosts = ["huggingface.co", "*.huggingface.co", "*.hf.co"];

    /// <summary>Merges the broker and host outputs, writes files.json with every hash, and zips the result.</summary>
    public static int PackRuntime(Args a)
    {
        string version = a.Required("version");
        if (!LocalAIPaths.IsSafeName(version))
        {
            throw new ToolException($"'{version}' is not a valid version.");
        }

        string staging = Path.Combine(Path.GetTempPath(), "localai-pack-" + Guid.NewGuid().ToString("N"));
        string? rid = a.Optional("rid");
        try
        {
            foreach (var source in new[] { a.Required("broker"), a.Required("host") })
            {
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(source, file);
                    if (relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".lib", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(relative) == "files.json")
                    {
                        continue;
                    }

                    // Framework-dependent builds carry natives for every OS; keep only the target RID's.
                    var segments = relative.Split(Path.DirectorySeparatorChar);
                    if (rid is not null && segments.Length > 2 && segments[0] == "runtimes" && segments[1] != rid)
                    {
                        continue;
                    }

                    string target = Path.Combine(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target) && SecureDownloader.Sha256OfFile(target) != SecureDownloader.Sha256OfFile(file))
                    {
                        throw new ToolException($"{relative} differs between the broker and host outputs. Publish both with the same runtime settings.");
                    }

                    File.Copy(file, target, overwrite: true);
                }
            }

            foreach (var required in new[] { "broker.exe", "host.exe" })
            {
                if (!File.Exists(Path.Combine(staging, required)))
                {
                    throw new ToolException($"{required} is missing from the inputs.");
                }
            }

            var list = new RuntimeFileList { Version = version };
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                list.Files.Add(new RuntimeFileEntry { Path = Path.GetRelativePath(staging, file).Replace('\\', '/'), Size = new FileInfo(file).Length, Sha256 = SecureDownloader.Sha256OfFile(file) });
            }

            File.WriteAllText(Path.Combine(staging, "files.json"), JsonSerializer.Serialize(list, TrustJsonContext.Default.RuntimeFileList));
            string output = Path.GetFullPath(a.Required("out"));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Delete(output);
            ZipFile.CreateFromDirectory(staging, output, CompressionLevel.Optimal, includeBaseDirectory: false);
            Console.WriteLine($"Packed runtime {version}: {list.Files.Count} files, {new FileInfo(output).Length:N0} bytes -> {output}");
            return 0;
        }
        finally
        {
            RuntimeInstaller.TryDeleteDirectory(staging);
        }
    }

    public static int ReleaseManifest(Args a)
    {
        // One or more runtime zips (runtime-win-x64.zip, runtime-win-arm64.zip); URLs are <zip-base-url><file name>.
        var zips = a.All("runtime-zip");
        if (zips.Count == 0)
        {
            throw new ToolException("--runtime-zip is required.");
        }

        string zipBase = a.Optional("zip-base-url") ?? "";
        var manifest = new ReleaseManifest
        {
            Channel = a.Required("channel"),
            Version = a.Required("version"),
            MinVersion = a.Optional("min-version"),
            Sequence = a.Long("sequence", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            Expires = DateTimeOffset.UtcNow.AddDays(a.Int("expires-days", 30)),
            RolloutPercent = a.Int("rollout", 100),
            KillSwitch = a.List("kill-switch"),
            RevokedKeys = a.List("revoked-keys"),
            DeprecatedContracts = a.List("deprecated"),
            DownloadHosts = a.List("download-hosts") is { Count: > 0 } hosts ? hosts : [.. DefaultDownloadHosts],
            CatalogUrl = a.Optional("catalog-url") ?? "../../catalog/catalog.json",
            RedirectRootUrl = a.Optional("redirect-root-url"),
            Files = zips.Select(zip => new ManifestFile
            {
                Name = Path.GetFileName(zip),
                Url = zips.Count == 1 && a.Optional("zip-url") is { } single ? single : zipBase + Path.GetFileName(zip),
                Size = new FileInfo(zip).Length,
                Sha256 = SecureDownloader.Sha256OfFile(zip),
            }).ToList(),
        };
        File.WriteAllText(a.Required("out"), JsonSerializer.Serialize(manifest, TrustJsonContext.Default.ReleaseManifest));
        Console.WriteLine($"Release manifest {manifest.Channel} {manifest.Version} (sequence {manifest.Sequence}, expires {manifest.Expires:yyyy-MM-dd}).");
        return 0;
    }

    /// <summary>Re-issues a manifest for another channel (for example preview to stable) with a new sequence and expiry.</summary>
    public static async Task<int> PromoteAsync(Args a)
    {
        string from = a.Required("from");
        byte[] signed = from.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? await new HttpClient().GetByteArrayAsync(from)
            : await File.ReadAllBytesAsync(from);
        var source = JsonSerializer.Deserialize(Convert.FromBase64String(TrustStore.Parse(signed).Payload), TrustJsonContext.Default.ReleaseManifest)
            ?? throw new ToolException("The source manifest is empty.");
        source.Channel = a.Required("channel");
        source.Sequence = a.Long("sequence", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        source.Expires = DateTimeOffset.UtcNow.AddDays(a.Int("expires-days", 30));
        source.RolloutPercent = a.Int("rollout", source.RolloutPercent);
        if (a.Optional("kill-switch") is not null)
        {
            source.KillSwitch = a.List("kill-switch");
        }

        if (a.Optional("min-version") is { } min)
        {
            source.MinVersion = min;
        }

        File.WriteAllText(a.Required("out"), JsonSerializer.Serialize(source, TrustJsonContext.Default.ReleaseManifest));
        Console.WriteLine($"Promoted {source.Version} to {source.Channel} (sequence {source.Sequence}, rollout {source.RolloutPercent}%).");
        return 0;
    }

    /// <summary>Checks a published site: the manifest and catalog verify, and optionally the runtime zip matches its hash.</summary>
    public static async Task<int> VerifySiteAsync(Args a)
    {
        string root = a.Required("root");
        var config = new RuntimeConfig { RootUrl = root, Channel = a.Required("channel") };
        var roots = TrustRoots.Production.Concat(a.List("trust-key")).Select(k => { try { return Convert.FromBase64String(k); } catch (FormatException) { return []; } }).ToList();
        string temp = Path.Combine(Path.GetTempPath(), "localai-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new LocalAIPaths(temp);
            using var http = SecureDownloader.CreateHttpClient("localai-tools");
            var installer = new RuntimeInstaller(paths, config, new TrustStore(roots), http, devMode: true);
            var manifest = await installer.FetchManifestAsync(CancellationToken.None);
            Console.WriteLine($"OK  release.json {manifest.Channel} {manifest.Version} sequence {manifest.Sequence}, expires {manifest.Expires:yyyy-MM-dd}");
            if (manifest.Expires < DateTimeOffset.UtcNow.AddDays(7))
            {
                Console.WriteLine("WARN release.json expires within 7 days; re-sign it.");
            }

            if (a.Flag("download"))
            {
                string version = await installer.InstallAsync(manifest, null, CancellationToken.None);
                Console.WriteLine($"OK  runtime.zip {version} verified and extracted");
            }

            if (manifest.CatalogUrl is { } catalogUrl)
            {
                byte[] bytes = await installer.GetSmallFileAsync(new Uri(installer.ManifestUri, catalogUrl), CancellationToken.None);
                byte[] payload = installer.Trust.Verify(TrustStore.Parse(bytes), TrustPurposes.Catalog, DateTimeOffset.UtcNow);
                var catalog = JsonSerializer.Deserialize(payload, TrustJsonContext.Default.CatalogDocument)!;
                Console.WriteLine($"OK  catalog.json sequence {catalog.Sequence}, {catalog.Models.Count} models, expires {catalog.Expires:yyyy-MM-dd}");
            }

            return 0;
        }
        catch (Exception ex) when (ex is TrustException or HttpRequestException or RpcException or IOException)
        {
            Console.Error.WriteLine($"FAIL {ex.Message}");
            return 1;
        }
        finally
        {
            RuntimeInstaller.TryDeleteDirectory(temp);
        }
    }
}
