using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace CommunityToolkit.AI.Protocol.Trust;

/// <summary>
/// Resumable, hash-verified downloads with a host allow-list that is enforced on every redirect hop.
/// Redirect targets are resolved per attempt and never cached, because CDN redirect URLs expire.
/// </summary>
internal sealed class SecureDownloader
{
    private const int MaxRedirects = 8;
    private const int MaxAttempts = 4;
    private readonly HttpClient _http;
    private readonly Func<Uri, bool> _isHostAllowed;
    private readonly bool _allowFileUrls;

    public SecureDownloader(HttpClient http, Func<Uri, bool> isHostAllowed, bool allowFileUrls = false)
    {
        _http = http;
        _isHostAllowed = isHostAllowed;
        _allowFileUrls = allowFileUrls;
    }

    /// <summary>Creates an HttpClient that does not follow redirects on its own.</summary>
    public static HttpClient CreateHttpClient(string userAgent)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseProxy = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    /// <summary>Matches a host against an allow-list. A leading "*." matches any subdomain.</summary>
    public static bool HostMatches(string host, IEnumerable<string> allowList)
    {
        foreach (var entry in allowList)
        {
            if (entry.StartsWith("*.", StringComparison.Ordinal))
            {
                if (host.EndsWith(entry[1..], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(host, entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string Sha256OfFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Downloads <paramref name="url"/> into <paramref name="destination"/>, resuming from <paramref name="partialPath"/>.
    /// The file is moved into place only after its size and SHA-256 match.
    /// </summary>
    public async Task DownloadAsync(Uri url, string destination, long expectedSize, string expectedSha256, string partialPath, Action<long>? bytesWritten, CancellationToken ct)
    {
        if (File.Exists(destination) && new FileInfo(destination).Length == expectedSize && Sha256OfFile(destination) == expectedSha256.ToLowerInvariant())
        {
            bytesWritten?.Invoke(expectedSize);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        Exception? last = null;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await DownloadToPartialAsync(url, expectedSize, partialPath, bytesWritten, ct).ConfigureAwait(false);
                last = null;
                break;
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException && !ct.IsCancellationRequested)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt * attempt), ct).ConfigureAwait(false);
            }
        }

        if (last is not null)
        {
            throw new RpcException(ErrorCodes.Offline, $"Download of {url} failed: {last.Message}");
        }

        long length = new FileInfo(partialPath).Length;
        string actual = Sha256OfFile(partialPath);
        if (length != expectedSize || actual != expectedSha256.ToLowerInvariant())
        {
            File.Delete(partialPath);
            throw new RpcException(ErrorCodes.IntegrityFailure, $"Integrity check failed for {url}: expected {expectedSize} bytes with SHA-256 {expectedSha256}, got {length} bytes with {actual}.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(partialPath, destination, overwrite: true);
    }

    private async Task DownloadToPartialAsync(Uri url, long expectedSize, string partialPath, Action<long>? bytesWritten, CancellationToken ct)
    {
        long existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existing > expectedSize)
        {
            File.Delete(partialPath);
            existing = 0;
        }

        bytesWritten?.Invoke(existing);
        if (existing == expectedSize)
        {
            return;
        }

        if (url.IsFile)
        {
            if (!_allowFileUrls)
            {
                throw new RpcException(ErrorCodes.IntegrityFailure, "file:// URLs are only allowed in dev mode.");
            }

            await using var source = new FileStream(url.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            source.Seek(existing, SeekOrigin.Begin);
            await CopyAsync(source, partialPath, append: existing > 0, existing, expectedSize, bytesWritten, ct).ConfigureAwait(false);
            return;
        }

        Uri current = url;
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            EnsureAllowed(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (existing > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existing, null);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(partialPath);
                throw new IOException("The server rejected the resume range; restarting.");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new RpcException(ErrorCodes.NotSupported, $"{current.Host} requires authentication. Gated or private files are not supported.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new RpcException(ErrorCodes.ModelUnknown, $"{current} was not found.");
            }

            response.EnsureSuccessStatusCode();
            bool append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!append)
            {
                existing = 0;
                bytesWritten?.Invoke(0);
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await CopyAsync(body, partialPath, append, existing, expectedSize, bytesWritten, ct).ConfigureAwait(false);
            return;
        }

        throw new IOException($"Too many redirects for {url}.");
    }

    private void EnsureAllowed(Uri uri)
    {
        bool secure = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        if (!secure || !_isHostAllowed(uri))
        {
            throw new RpcException(ErrorCodes.IntegrityFailure, $"Downloads from {uri.Scheme}://{uri.Host} are not allowed.");
        }
    }

    private static async Task CopyAsync(Stream source, string partialPath, bool append, long existing, long expectedSize, Action<long>? bytesWritten, CancellationToken ct)
    {
        await using var target = new FileStream(partialPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var buffer = new byte[1 << 16];
        long total = existing;
        int n;
        while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += n;
            if (total > expectedSize)
            {
                throw new RpcException(ErrorCodes.IntegrityFailure, "The server sent more bytes than the catalog size.");
            }

            await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            bytesWritten?.Invoke(total);
        }
    }
}

/// <summary>Zip extraction that rejects path traversal, absolute paths and oversized payloads.</summary>
internal static class SafeZip
{
    public static void ExtractToDirectory(string zipPath, string destination, long maxTotalBytes = 2L * 1024 * 1024 * 1024)
    {
        string root = Path.GetFullPath(destination);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        Directory.CreateDirectory(root);
        using var archive = ZipFile.OpenRead(zipPath);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(s => s == ".."))
            {
                throw new InvalidDataException($"Unsafe zip entry '{entry.FullName}'.");
            }

            string target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Zip entry '{entry.FullName}' escapes the destination.");
            }

            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            total += entry.Length;
            if (total > maxTotalBytes)
            {
                throw new InvalidDataException("The zip is larger than allowed.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }
}
