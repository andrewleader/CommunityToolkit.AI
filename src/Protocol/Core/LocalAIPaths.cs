using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Serialization;

namespace CommunityToolkit.AI.Protocol;

/// <summary>
/// Per-user install layout (spec section 6) and the names of the per-user kernel objects.
/// </summary>
internal sealed class LocalAIPaths
{
    public LocalAIPaths(string? home = null)
    {
        Home = Path.GetFullPath(string.IsNullOrEmpty(home) ? DefaultHome : home);
        IsDefaultHome = string.Equals(Home.TrimEnd('\\'), DefaultHome.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    public static string DefaultHome =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommunityToolkit", "LocalAI");

    public string Home { get; }

    public bool IsDefaultHome { get; }

    public string RuntimeDir => Path.Combine(Home, "runtime");

    public string CurrentFile => Path.Combine(RuntimeDir, "current.txt");

    public string ModelsDir => Path.Combine(Home, "models");

    public string BlobsDir => Path.Combine(ModelsDir, ".blobs");

    public string EpsDir => Path.Combine(Home, "eps");

    public string StagingDir => Path.Combine(Home, "staging");

    public string StateDir => Path.Combine(Home, "state");

    public string LogsDir => Path.Combine(Home, "logs");

    public string ConfigFile => Path.Combine(StateDir, "config.json");

    public string InstallStateFile => Path.Combine(StateDir, "install-progress.json");

    public string UpdateStateFile => Path.Combine(StateDir, "update.json");

    public string LaunchHistoryFile => Path.Combine(StateDir, "launches.json");

    public string LicensesFile => Path.Combine(StateDir, "licenses.json");

    public string CatalogCacheFile => Path.Combine(StateDir, "catalog.json");

    public string ReleaseCacheFile => Path.Combine(StateDir, "release.json");

    public string RuntimeVersionDir(string version) => Path.Combine(RuntimeDir, version);

    public string ModelDir(string id, string version) => Path.Combine(ModelsDir, id, version);

    public string UserSid => WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;

    /// <summary>Appended to kernel object names when a non-default home is used, so test installs never collide with the real one.</summary>
    private string Suffix => IsDefaultHome ? "" : "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Home.ToUpperInvariant()))).Substring(0, 12);

    public string BrokerPipeName => $"LocalAI-{UserSid}-v{ProtocolInfo.CoreMajor}{Suffix}";

    public string BrokerMutexName => $"Local\\LocalAI-Broker-{UserSid}{Suffix}";

    public string InstallMutexName => $"Local\\LocalAI-Install-{UserSid}{Suffix}";

    public string HostPipeName(string key) => $"LocalAI-{UserSid}-host-{key}{Suffix}";

    /// <summary>Reads the runtime version named by current.txt, or null.</summary>
    public string? ReadCurrentVersion()
    {
        try
        {
            if (!File.Exists(CurrentFile))
            {
                return null;
            }

            string version = File.ReadAllText(CurrentFile).Trim();
            return IsSafeName(version) ? version : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Atomically replaces current.txt.</summary>
    public void WriteCurrentVersion(string version)
    {
        if (!IsSafeName(version))
        {
            throw new ArgumentException("Invalid runtime version.", nameof(version));
        }

        AtomicFile.WriteAllText(CurrentFile, version);
    }

    /// <summary>A version or id that is safe to use as a single path segment.</summary>
    public static bool IsSafeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value is "." or "..")
        {
            return false;
        }

        foreach (char ch in value)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or '+'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Settings the bootstrapper hands to the broker and the scheduled updater (state\config.json).</summary>
internal sealed class RuntimeConfig
{
    [JsonPropertyName("rootUrl")] public string RootUrl { get; set; } = "";
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";

    /// <summary>Extra root public keys trusted in dev mode only (base64).</summary>
    [JsonPropertyName("devTrustKeys")] public List<string> DevTrustKeys { get; set; } = new();
}

internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents) => WriteAllBytes(path, Encoding.UTF8.GetBytes(contents));

    public static void WriteAllBytes(string path, byte[] contents)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
