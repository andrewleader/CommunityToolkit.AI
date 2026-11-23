using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CommunityToolkit.AI.Protocol.Trust;

/// <summary>
/// Fetches and verifies the signed release manifest and installs runtime versions side by side.
/// Shared by the client bootstrapper (first install) and the broker's updater.
/// </summary>
internal sealed class RuntimeInstaller
{
    private readonly LocalAIPaths _paths;
    private readonly HttpClient _http;
    private readonly bool _devMode;

    public RuntimeInstaller(LocalAIPaths paths, RuntimeConfig config, TrustStore trust, HttpClient http, bool devMode)
    {
        _paths = paths;
        Config = config;
        Trust = trust;
        _http = http;
        _devMode = devMode;
    }

    public RuntimeConfig Config { get; }

    public TrustStore Trust { get; }

    public Uri RootUri => new(Config.RootUrl.EndsWith('/') ? Config.RootUrl : Config.RootUrl + "/");

    public Uri ManifestUri => new(RootUri, $"runtime/{Config.Channel}/release.json");

    /// <summary>Whether this user falls inside a staged rollout percentage. Stable per user.</summary>
    public bool IsInRollout(int percent)
    {
        if (percent >= 100)
        {
            return true;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(_paths.UserSid));
        return BitConverter.ToUInt32(hash, 0) % 100 < (uint)Math.Max(0, percent);
    }

    public async Task<ReleaseManifest> FetchManifestAsync(CancellationToken ct)
    {
        byte[] bytes = await GetSmallFileAsync(ManifestUri, ct).ConfigureAwait(false);
        var manifest = VerifyManifest(bytes, DateTimeOffset.UtcNow);
        AtomicFile.WriteAllBytes(_paths.ReleaseCacheFile, bytes);
        return manifest;
    }

    public ReleaseManifest VerifyManifest(byte[] signedBytes, DateTimeOffset now)
    {
        var document = TrustStore.Parse(signedBytes);
        byte[] payload = Trust.Verify(document, TrustPurposes.Runtime, now);
        var manifest = JsonSerializer.Deserialize(payload, TrustJsonContext.Default.ReleaseManifest) ?? throw new TrustException("Empty release manifest.");
        if (!string.Equals(manifest.Channel, Config.Channel, StringComparison.Ordinal))
        {
            throw new TrustException($"The manifest is for channel '{manifest.Channel}', not '{Config.Channel}'.");
        }

        if (!LocalAIPaths.IsSafeName(manifest.Version))
        {
            throw new TrustException("The manifest has an invalid version.");
        }

        Freshness.Check("release:" + Config.Channel, manifest.Sequence, manifest.Expires, Path.Combine(_paths.StateDir, "sequences.json"), now, persist: true);
        Trust.Revoke(manifest.RevokedKeys);
        return manifest;
    }

    /// <summary>Loads the last verified manifest from disk, re-verifying it. Null if absent or no longer valid.</summary>
    public ReleaseManifest? TryLoadCachedManifest()
    {
        try
        {
            return File.Exists(_paths.ReleaseCacheFile) ? VerifyManifest(File.ReadAllBytes(_paths.ReleaseCacheFile), DateTimeOffset.UtcNow) : null;
        }
        catch (Exception ex) when (ex is TrustException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Downloads, verifies and extracts the runtime into runtime\&lt;version&gt;. Does not flip current.txt.</summary>
    public async Task<string> InstallAsync(ReleaseManifest manifest, Action<long, long>? progress, CancellationToken ct)
    {
        if (manifest.KillSwitch.Contains(manifest.Version))
        {
            throw new TrustException($"Runtime {manifest.Version} is disabled by the kill switch.");
        }

        string versionDir = _paths.RuntimeVersionDir(manifest.Version);
        if (VerifyRuntimeDirectory(versionDir, manifest.Version, fullHash: true))
        {
            return manifest.Version;
        }

        var zip = manifest.RuntimeZip ?? throw new TrustException("The manifest has no runtime.zip.");
        var zipUri = new Uri(ManifestUri, zip.Url);
        string zipPath = Path.Combine(_paths.StagingDir, $"runtime-{manifest.Version}.zip");
        var downloader = new SecureDownloader(_http, uri => uri.Host.Equals(RootUri.Host, StringComparison.OrdinalIgnoreCase), allowFileUrls: _devMode);
        await downloader.DownloadAsync(zipUri, zipPath, zip.Size, zip.Sha256, zipPath + ".partial", done => progress?.Invoke(done, zip.Size), ct).ConfigureAwait(false);

        string extractDir = Path.Combine(_paths.StagingDir, $"runtime-{manifest.Version}-{Guid.NewGuid():N}");
        try
        {
            SafeZip.ExtractToDirectory(zipPath, extractDir);
            if (!VerifyRuntimeDirectory(extractDir, manifest.Version, fullHash: true))
            {
                throw new TrustException("The extracted runtime does not match its files.json.");
            }

            if (Directory.Exists(versionDir))
            {
                Directory.Delete(versionDir, recursive: true);
            }

            Directory.CreateDirectory(_paths.RuntimeDir);
            Directory.Move(extractDir, versionDir);
        }
        finally
        {
            TryDeleteDirectory(extractDir);
        }

        TryDeleteFile(zipPath);
        return manifest.Version;
    }

    /// <summary>
    /// Checks files.json in a runtime folder. With <paramref name="fullHash"/> every file is hashed; otherwise only the executables.
    /// </summary>
    public static bool VerifyRuntimeDirectory(string dir, string? expectedVersion, bool fullHash)
    {
        try
        {
            string listPath = Path.Combine(dir, "files.json");
            if (!File.Exists(listPath))
            {
                return false;
            }

            var list = JsonSerializer.Deserialize(File.ReadAllBytes(listPath), TrustJsonContext.Default.RuntimeFileList);
            if (list is null || (expectedVersion is not null && list.Version != expectedVersion))
            {
                return false;
            }

            string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in list.Files)
            {
                string full = Path.GetFullPath(Path.Combine(root, file.Path));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                {
                    return false;
                }

                listed.Add(full);
                bool isExecutable = full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                if ((fullHash || isExecutable && Path.GetFileName(full).StartsWith("broker", StringComparison.OrdinalIgnoreCase)) &&
                    (new FileInfo(full).Length != file.Size || SecureDownloader.Sha256OfFile(full) != file.Sha256.ToLowerInvariant()))
                {
                    return false;
                }
            }

            if (fullHash)
            {
                // No unlisted code may sit next to the runtime.
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (!listed.Contains(Path.GetFullPath(file)) && !file.EndsWith("files.json", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            return listed.Any(f => Path.GetFileName(f).Equals("broker.exe", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<byte[]> GetSmallFileAsync(Uri uri, CancellationToken ct)
    {
        if (uri.IsFile)
        {
            if (!_devMode)
            {
                throw new TrustException("file:// URLs are only allowed in dev mode.");
            }

            return await File.ReadAllBytesAsync(uri.LocalPath, ct).ConfigureAwait(false);
        }

        Uri current = uri;
        for (int hop = 0; hop < 5; hop++)
        {
            if (!(current.Scheme == Uri.UriSchemeHttps || (current.Scheme == Uri.UriSchemeHttp && current.IsLoopback)))
            {
                throw new TrustException($"Refusing insecure URL {current}.");
            }

            using var response = await _http.GetAsync(current, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new RpcException(ErrorCodes.RuntimeUnavailable, $"{current} was not found.");
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }

        throw new IOException($"Too many redirects for {uri}.");
    }

    public static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
