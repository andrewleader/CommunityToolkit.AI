using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.AI.Protocol.Trust;
using CommunityToolkit.AI.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CommunityToolkit.AI.Runtime.Tests;

/// <summary>
/// A local copy of the Pages site signed with throwaway dev keys: release manifests, the runtime zip built from this
/// repo's broker and host, a signed catalog, and the tiny test model files. The client runs in dev mode against it.
/// </summary>
public sealed class SiteFixture : IAsyncLifetime
{
    public const string RuntimeVersion = "1.0.0-test";
    private readonly byte[] _rootSeed = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, (byte[] Seed, KeyCertificate Cert)> _signers = new();
    private WebApplication? _app;
    private long _sequence = 100;

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "localai-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public string SiteDir => Path.Combine(Root, "site");

    public string RootUrl { get; private set; } = "";

    public string TrustKey => Convert.ToBase64String(Ed25519.GetPublicKey(_rootSeed));

    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CommunityToolkit.AI.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
        }
    }

    public async Task InitializeAsync()
    {
        foreach (var purpose in new[] { TrustPurposes.Runtime, TrustPurposes.Catalog })
        {
            byte[] seed = RandomNumberGenerator.GetBytes(32);
            _signers[purpose] = (seed, DocumentSigner.Certify(_rootSeed, Ed25519.GetPublicKey(seed), [purpose], DateTimeOffset.UtcNow.AddDays(1)));
        }

        Directory.CreateDirectory(SiteDir);
        PackRuntime(RuntimeVersion);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(SiteDir),
            ServeUnknownFileTypes = true,
            ContentTypeProvider = new FileExtensionContentTypeProvider(),
        });
        await _app.StartAsync();
        RootUrl = _app.Urls.First() + "/";

        WriteModelFile("models/echo/weights.bin", 200_000);
        PublishCatalog();
        PublishRelease("test", RuntimeVersion);
    }

    /// <summary>Points the client at a fresh install home and channel. Each test gets its own broker.</summary>
    public string UseFreshHome(string channel = "test", params (string Name, string Value)[] extraEnv)
    {
        string home = Path.Combine(Root, "home-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("LOCALAI_HOME", home);
        Environment.SetEnvironmentVariable("LOCALAI_DEV_ROOT_URL", RootUrl);
        Environment.SetEnvironmentVariable("LOCALAI_DEV_TRUST_KEY", TrustKey);
        Environment.SetEnvironmentVariable("LOCALAI_CHANNEL", channel);
        Environment.SetEnvironmentVariable("LOCALAI_BROKER_IDLE_SECONDS", "120");
        Environment.SetEnvironmentVariable("LOCALAI_HOST_IDLE_SECONDS", "120");
        Environment.SetEnvironmentVariable("LOCALAI_UPDATE_INITIAL_DELAY_SECONDS", "3600");
        Environment.SetEnvironmentVariable("LOCALAI_UPDATE_INTERVAL_SECONDS", null);
        Environment.SetEnvironmentVariable("LOCALAI_BROKER_TICK_MS", null);
        foreach (var (name, value) in extraEnv)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        LocalAIRuntime.EnableDevMode = true;
        return home;
    }

    public void PackRuntime(string version) =>
        Assert.Equal(0, RuntimeCommands.PackRuntime(new Args(["--broker", BuildOutput("Broker"), "--host", BuildOutput("Host"), "--version", version, "--rid", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "--out", Path.Combine(SiteDir, "runtime", version, "runtime.zip")])));

    public void PublishRelease(string channel, string version)
    {
        string zip = Path.Combine(SiteDir, "runtime", version, "runtime.zip");
        var manifest = new ReleaseManifest
        {
            Channel = channel,
            Version = version,
            Sequence = Interlocked.Increment(ref _sequence),
            Expires = DateTimeOffset.UtcNow.AddDays(1),
            DownloadHosts = ["127.0.0.1"],
            CatalogUrl = "../../catalog/catalog.json",
            Files = [new ManifestFile { Name = "runtime.zip", Url = $"../{version}/runtime.zip", Size = new FileInfo(zip).Length, Sha256 = SecureDownloader.Sha256OfFile(zip) }],
        };
        WriteSigned(Path.Combine("runtime", channel, "release.json"), TrustPurposes.Runtime, JsonSerializer.SerializeToUtf8Bytes(manifest, TrustJsonContext.Default.ReleaseManifest));
    }

    private void PublishCatalog()
    {
        var echoFile = FileEntry("models/echo/weights.bin", "weights.bin");
        CatalogModel Echo(string id, string task, string adapter, Dictionary<string, string>? parameters = null, CatalogFile? file = null) => new()
        {
            Id = id,
            Task = task,
            Version = "1.0.0",
            DisplayName = id,
            License = new CatalogLicense { Id = "MIT", Url = "https://opensource.org/license/mit", RequiresAcceptance = true },
            Adapter = new CatalogAdapter { Id = adapter, Parameters = parameters ?? new() },
            Files = [file ?? echoFile],
        };

        var corrupt = FileEntry("models/echo/weights.bin", "weights.bin");
        corrupt.Sha256 = new string('a', 64);
        var catalog = new CatalogDocument
        {
            Sequence = Interlocked.Increment(ref _sequence),
            Expires = DateTimeOffset.UtcNow.AddDays(1),
            Models =
            [
                Echo("test-echo-chat", "chat", "test-echo", new() { ["allowCrash"] = "true" }),
                Echo("test-slow-chat", "chat", "test-echo", new() { ["tokenDelayMs"] = "150" }),
                Echo("test-other-chat", "chat", "test-echo"),
                Echo("test-corrupt-chat", "chat", "test-echo", file: corrupt),
                Echo("test-ocr", "ocr", "test-ocr-missing"),
            ],
        };
        WriteSigned(Path.Combine("catalog", "catalog.json"), TrustPurposes.Catalog, JsonSerializer.SerializeToUtf8Bytes(catalog, TrustJsonContext.Default.CatalogDocument));
    }

    private CatalogFile FileEntry(string sitePath, string modelPath)
    {
        string full = Path.Combine(SiteDir, sitePath);
        return new CatalogFile { Path = modelPath, Url = RootUrl + sitePath, Size = new FileInfo(full).Length, Sha256 = SecureDownloader.Sha256OfFile(full) };
    }

    private void WriteModelFile(string sitePath, int size)
    {
        string full = Path.Combine(SiteDir, sitePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, RandomNumberGenerator.GetBytes(size));
    }

    private void WriteSigned(string sitePath, string purpose, byte[] payload)
    {
        var (seed, cert) = _signers[purpose];
        string full = Path.Combine(SiteDir, sitePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, JsonSerializer.Serialize(DocumentSigner.Sign(payload, purpose, seed, cert), TrustJsonContext.Default.SignedDocument));
    }

    private static string BuildOutput(string project)
    {
        // Tests run from tests\X\bin\<config>\<tfm>; the runtime projects build to the same configuration.
        var testDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\'));
        string tfm = testDir.Name;
        string config = testDir.Parent!.Name;
        string dir = Path.Combine(RepoRoot, "src", project, "bin", config, tfm);
        return Directory.Exists(dir) ? dir : throw new DirectoryNotFoundException(dir);
    }

    /// <summary>Processes with the given image name whose executable lives under a directory.</summary>
    public static List<Process> ProcessesUnder(string dir, string name) =>
        Process.GetProcessesByName(name).Where(p =>
        {
            try
            {
                return p.MainModule?.FileName.StartsWith(dir, StringComparison.OrdinalIgnoreCase) == true;
            }
            catch (Exception)
            {
                return false;
            }
        }).ToList();

    public async Task DisposeAsync()
    {
        foreach (var process in ProcessesUnder(Root, "broker").Concat(ProcessesUnder(Root, "host")))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception)
            {
            }
        }

        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
        }
    }
}
