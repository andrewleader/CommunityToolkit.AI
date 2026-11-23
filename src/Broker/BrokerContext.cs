using System.Reflection;
using System.Text.Json;
using CommunityToolkit.AI.Protocol;
using CommunityToolkit.AI.Protocol.Runtime;
using CommunityToolkit.AI.Protocol.Trust;

namespace CommunityToolkit.AI.Broker;

/// <summary>Shared state for one broker process.</summary>
internal sealed class BrokerContext : IDisposable
{
    public BrokerContext(LocalAIPaths paths)
    {
        Paths = paths;
        Policy = EnterprisePolicy.Read();
        Config = LoadConfig(paths);
        if (Policy.RootUrl is { Length: > 0 } root)
        {
            Config.RootUrl = root;
        }

        if (Policy.Channel is { Length: > 0 } channel)
        {
            Config.Channel = channel;
        }

        RuntimeDir = AppContext.BaseDirectory;
        RuntimeVersion = ReadRuntimeVersion(RuntimeDir);
        Http = SecureDownloader.CreateHttpClient($"CommunityToolkit.AI.Broker/{RuntimeVersion}");
        var roots = TrustRoots.Production.Select(TryDecode).Where(k => k.Length == Ed25519.PublicKeySize).ToList();
        roots.AddRange(Config.DevTrustKeys.Select(TryDecode).Where(k => k.Length == Ed25519.PublicKeySize));
        Installer = new RuntimeInstaller(paths, Config, new TrustStore(roots), Http, DevMode);
    }

    public LocalAIPaths Paths { get; }

    public EnterprisePolicy Policy { get; }

    public RuntimeConfig Config { get; }

    /// <summary>Dev mode is on when the bootstrapper passed dev trust keys (only possible when the app opted in).</summary>
    public bool DevMode => Config.DevTrustKeys.Count > 0;

    public string RuntimeDir { get; }

    public string RuntimeVersion { get; }

    public HttpClient Http { get; }

    public RuntimeInstaller Installer { get; }

    public static RuntimeConfig LoadConfig(LocalAIPaths paths)
    {
        try
        {
            if (File.Exists(paths.ConfigFile) &&
                JsonSerializer.Deserialize(File.ReadAllBytes(paths.ConfigFile), CoreJsonContext.Default.RuntimeConfig) is { RootUrl.Length: > 0 } config)
            {
                return config;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Warn($"Could not read config.json: {ex.Message}");
        }

        return new RuntimeConfig { RootUrl = "https://andrewleader.github.io/CommunityToolkit.AI/", Channel = "preview" };
    }

    public static string ReadRuntimeVersion(string runtimeDir)
    {
        try
        {
            string list = Path.Combine(runtimeDir, "files.json");
            if (File.Exists(list) && JsonSerializer.Deserialize(File.ReadAllBytes(list), TrustJsonContext.Default.RuntimeFileList) is { Version.Length: > 0 } files)
            {
                return files.Version;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }

        return typeof(BrokerContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0-dev";
    }

    private static byte[] TryDecode(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    public void Dispose() => Http.Dispose();
}
