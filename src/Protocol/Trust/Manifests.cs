using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunityToolkit.AI.Protocol.Trust;

/// <summary>Payload of runtime/&lt;channel&gt;/release.json (spec section 8).</summary>
internal sealed class ReleaseManifest
{
    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    /// <summary>Installed runtimes older than this must update before serving clients.</summary>
    [JsonPropertyName("minVersion")] public string? MinVersion { get; set; }

    /// <summary>Monotonic. A manifest with a lower sequence than the stored one is refused (anti-rollback).</summary>
    [JsonPropertyName("sequence")] public long Sequence { get; set; }

    /// <summary>A manifest past this time is refused (anti-freeze).</summary>
    [JsonPropertyName("expires")] public DateTimeOffset Expires { get; set; }

    [JsonPropertyName("rolloutPercent")] public int RolloutPercent { get; set; } = 100;

    /// <summary>Runtime versions that must not run.</summary>
    [JsonPropertyName("killSwitch")] public List<string> KillSwitch { get; set; } = new();

    [JsonPropertyName("revokedKeys")] public List<string> RevokedKeys { get; set; } = new();

    /// <summary>Hosts model files may be downloaded from. A leading "*." matches any subdomain.</summary>
    [JsonPropertyName("downloadHosts")] public List<string> DownloadHosts { get; set; } = new();

    [JsonPropertyName("files")] public List<ManifestFile> Files { get; set; } = new();

    /// <summary>Deprecated task contract majors, written as task/major, for example chat/0.</summary>
    [JsonPropertyName("deprecatedContracts")] public List<string> DeprecatedContracts { get; set; } = new();

    /// <summary>Catalog location, relative to this manifest or absolute.</summary>
    [JsonPropertyName("catalogUrl")] public string? CatalogUrl { get; set; }

    /// <summary>If set, clients should use this root URL from now on (for example after a repo move).</summary>
    [JsonPropertyName("redirectRootUrl")] public string? RedirectRootUrl { get; set; }

    /// <summary>The runtime package for this machine: runtime-win-&lt;arch&gt;.zip, falling back to runtime.zip.</summary>
    public ManifestFile? RuntimeZip
    {
        get
        {
            string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
            return Files.FirstOrDefault(f => f.Name == $"runtime-win-{arch}.zip") ?? Files.FirstOrDefault(f => f.Name == "runtime.zip");
        }
    }
}

internal sealed class ManifestFile
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

/// <summary>files.json inside the runtime zip: every file and its hash, verified before execution.</summary>
internal sealed class RuntimeFileList
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("files")] public List<RuntimeFileEntry> Files { get; set; } = new();
}

internal sealed class RuntimeFileEntry
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

/// <summary>Payload of catalog/catalog.json (spec section 10).</summary>
internal sealed class CatalogDocument
{
    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
    [JsonPropertyName("sequence")] public long Sequence { get; set; }
    [JsonPropertyName("expires")] public DateTimeOffset Expires { get; set; }
    [JsonPropertyName("models")] public List<CatalogModel> Models { get; set; } = new();
}

internal sealed class CatalogModel
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("task")] public string Task { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("license")] public CatalogLicense License { get; set; } = new();
    [JsonPropertyName("minRuntimeVersion")] public string? MinRuntimeVersion { get; set; }
    [JsonPropertyName("hardware")] public CatalogHardware Hardware { get; set; } = new();
    [JsonPropertyName("adapter")] public CatalogAdapter Adapter { get; set; } = new();
    [JsonPropertyName("hfRepo")] public string? HfRepo { get; set; }
    [JsonPropertyName("hfRevision")] public string? HfRevision { get; set; }
    [JsonPropertyName("files")] public List<CatalogFile> Files { get; set; } = new();

    /// <summary>Feature flag: hidden entries resolve only in dev mode, so incomplete tasks can ship dark.</summary>
    [JsonPropertyName("hidden")] public bool Hidden { get; set; }

    [JsonIgnore] public long TotalSize => Files.Sum(f => f.Size);
}

internal sealed class CatalogLicense
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("requiresAcceptance")] public bool RequiresAcceptance { get; set; } = true;
}

internal sealed class CatalogHardware
{
    [JsonPropertyName("minMemoryMB")] public int MinMemoryMB { get; set; }

    /// <summary>Preferred execution providers in order, for example ["npu","gpu","cpu"].</summary>
    [JsonPropertyName("eps")] public List<string> Eps { get; set; } = ["cpu"];
}

internal sealed class CatalogAdapter
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("parameters")] public Dictionary<string, string> Parameters { get; set; } = new();
}

internal sealed class CatalogFile
{
    /// <summary>Relative path inside the model folder, using forward slashes.</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

/// <summary>Stored per model folder as model.json after a verified install.</summary>
internal sealed class InstalledModel
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("task")] public string Task { get; set; } = "";
    [JsonPropertyName("adapter")] public CatalogAdapter Adapter { get; set; } = new();
    [JsonPropertyName("files")] public List<CatalogFile> Files { get; set; } = new();
    [JsonPropertyName("installedAt")] public DateTimeOffset InstalledAt { get; set; }
    [JsonPropertyName("lastUsedAt")] public DateTimeOffset LastUsedAt { get; set; }
}

/// <summary>Highest sequence numbers seen, per document kind (anti-rollback).</summary>
internal sealed class SequenceState
{
    [JsonPropertyName("sequences")] public Dictionary<string, long> Sequences { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SignedDocument))]
[JsonSerializable(typeof(KeyCertificate))]
[JsonSerializable(typeof(ReleaseManifest))]
[JsonSerializable(typeof(RuntimeFileList))]
[JsonSerializable(typeof(CatalogDocument))]
[JsonSerializable(typeof(CatalogModel))]
[JsonSerializable(typeof(InstalledModel))]
[JsonSerializable(typeof(SequenceState))]
[JsonSerializable(typeof(List<CatalogModel>))]
internal sealed partial class TrustJsonContext : JsonSerializerContext
{
}

/// <summary>Anti-rollback and anti-freeze checks for signed documents.</summary>
internal static class Freshness
{
    public static void Check(string kind, long sequence, DateTimeOffset expires, string stateFile, DateTimeOffset now, bool persist)
    {
        if (expires <= now)
        {
            throw new TrustException($"The {kind} document expired at {expires:O}.");
        }

        var state = Load(stateFile);
        if (state.Sequences.TryGetValue(kind, out long stored) && sequence < stored)
        {
            throw new TrustException($"The {kind} document sequence {sequence} is older than the stored sequence {stored} (possible rollback).");
        }

        if (persist && (!state.Sequences.TryGetValue(kind, out stored) || sequence > stored))
        {
            state.Sequences[kind] = sequence;
            AtomicFile.WriteAllBytes(stateFile, JsonSerializer.SerializeToUtf8Bytes(state, TrustJsonContext.Default.SequenceState));
        }
    }

    private static SequenceState Load(string stateFile)
    {
        try
        {
            if (File.Exists(stateFile))
            {
                return JsonSerializer.Deserialize(File.ReadAllBytes(stateFile), TrustJsonContext.Default.SequenceState) ?? new SequenceState();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }

        return new SequenceState();
    }
}

/// <summary>Minimal SemVer 2 ordering for runtime and package versions.</summary>
internal static class SemVer
{
    public static int Compare(string? a, string? b)
    {
        if (a == b)
        {
            return 0;
        }

        if (a is null)
        {
            return -1;
        }

        if (b is null)
        {
            return 1;
        }

        Split(a, out var aCore, out var aPre);
        Split(b, out var bCore, out var bPre);
        for (int i = 0; i < Math.Max(aCore.Length, bCore.Length); i++)
        {
            long x = i < aCore.Length ? aCore[i] : 0;
            long y = i < bCore.Length ? bCore[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        if (aPre is null || bPre is null)
        {
            return aPre is null ? (bPre is null ? 0 : 1) : -1;
        }

        var aIds = aPre.Split('.');
        var bIds = bPre.Split('.');
        for (int i = 0; i < Math.Min(aIds.Length, bIds.Length); i++)
        {
            bool aNum = long.TryParse(aIds[i], out long an);
            bool bNum = long.TryParse(bIds[i], out long bn);
            int c = aNum && bNum ? an.CompareTo(bn) : aNum ? -1 : bNum ? 1 : string.CompareOrdinal(aIds[i], bIds[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return aIds.Length.CompareTo(bIds.Length);
    }

    private static void Split(string version, out long[] core, out string? prerelease)
    {
        string v = version.Split('+')[0];
        int dash = v.IndexOf('-');
        prerelease = dash >= 0 ? v[(dash + 1)..] : null;
        core = (dash >= 0 ? v[..dash] : v).Split('.').Select(p => long.TryParse(p, out long n) ? n : 0).ToArray();
    }
}
