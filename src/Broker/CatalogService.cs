using System.Text.Json;
using CommunityToolkit.AI.Protocol;
using CommunityToolkit.AI.Protocol.Runtime;
using CommunityToolkit.AI.Protocol.Trust;

namespace CommunityToolkit.AI.Broker;

/// <summary>Resolves model ids against the signed catalog, which updates independently of the runtime.</summary>
internal sealed class CatalogService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan MinRefetchInterval = TimeSpan.FromMinutes(1);
    private static readonly string[] DefaultDownloadHosts = ["huggingface.co", "*.huggingface.co", "*.hf.co"];
    private readonly BrokerContext _ctx;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ReleaseManifest? _manifest;
    private CatalogDocument? _catalog;
    private DateTimeOffset _catalogFetchedAt;

    public CatalogService(BrokerContext ctx) => _ctx = ctx;

    public ReleaseManifest? Manifest => _manifest;

    public IReadOnlyList<string> DownloadHosts
    {
        get
        {
            var hosts = (_manifest?.DownloadHosts is { Count: > 0 } listed ? listed : DefaultDownloadHosts.ToList()).ToList();
            if (_ctx.Policy.HuggingFaceMirror is { } mirror && Uri.TryCreate(mirror, UriKind.Absolute, out var mirrorUri))
            {
                hosts.Add(mirrorUri.Host);
            }

            return hosts;
        }
    }

    public async Task<ReleaseManifest?> GetManifestAsync(bool refresh, CancellationToken ct)
    {
        if (_manifest is not null && !refresh)
        {
            return _manifest;
        }

        try
        {
            _manifest = await _ctx.Installer.FetchManifestAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TrustException or RpcException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn($"Release manifest unavailable ({ex.Message}); using the cached copy.");
            _manifest ??= _ctx.Installer.TryLoadCachedManifest();
        }

        return _manifest;
    }

    public async Task<CatalogDocument?> GetCatalogAsync(bool forceRefresh, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var age = DateTimeOffset.UtcNow - _catalogFetchedAt;
            if (_catalog is not null && age < RefreshInterval && !(forceRefresh && age > MinRefetchInterval))
            {
                return _catalog;
            }

            _catalog ??= LoadCached();
            try
            {
                var manifest = await GetManifestAsync(refresh: _catalog is not null, ct).ConfigureAwait(false);
                if (manifest?.CatalogUrl is { Length: > 0 } catalogUrl)
                {
                    var uri = new Uri(_ctx.Installer.ManifestUri, catalogUrl);
                    byte[] bytes = await _ctx.Installer.GetSmallFileAsync(uri, ct).ConfigureAwait(false);
                    _catalog = Verify(bytes, persist: true);
                    AtomicFile.WriteAllBytes(_ctx.Paths.CatalogCacheFile, bytes);
                    _catalogFetchedAt = DateTimeOffset.UtcNow;
                    Log.Info($"Catalog sequence {_catalog.Sequence} loaded with {_catalog.Models.Count} models.");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TrustException or RpcException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                Log.Warn($"Catalog refresh failed: {ex.Message}");
                _catalogFetchedAt = DateTimeOffset.UtcNow - RefreshInterval + MinRefetchInterval;
            }

            return _catalog;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Finds a model by id. Refreshes the catalog once if the id is unknown.</summary>
    public async Task<CatalogModel?> FindAsync(string id, CancellationToken ct)
    {
        var model = Find(await GetCatalogAsync(false, ct).ConfigureAwait(false), id);
        if (model is null)
        {
            model = Find(await GetCatalogAsync(true, ct).ConfigureAwait(false), id);
        }

        return model;
    }

    /// <summary>Applies the enterprise Hugging Face mirror, if any.</summary>
    public Uri ResolveFileUrl(CatalogFile file)
    {
        string url = file.Url;
        if (_ctx.Policy.HuggingFaceMirror is { Length: > 0 } mirror && url.StartsWith("https://huggingface.co/", StringComparison.OrdinalIgnoreCase))
        {
            url = mirror.TrimEnd('/') + "/" + url["https://huggingface.co/".Length..];
        }

        return new Uri(url);
    }

    private CatalogModel? Find(CatalogDocument? catalog, string id) =>
        catalog?.Models.FirstOrDefault(m => m.Id == id && (!m.Hidden || _ctx.DevMode));

    private CatalogDocument? LoadCached()
    {
        try
        {
            return File.Exists(_ctx.Paths.CatalogCacheFile) ? Verify(File.ReadAllBytes(_ctx.Paths.CatalogCacheFile), persist: false) : null;
        }
        catch (Exception ex) when (ex is TrustException or JsonException or IOException)
        {
            Log.Warn($"Cached catalog rejected: {ex.Message}");
            return null;
        }
    }

    private CatalogDocument Verify(byte[] signed, bool persist)
    {
        var document = TrustStore.Parse(signed);
        byte[] payload = _ctx.Installer.Trust.Verify(document, TrustPurposes.Catalog, DateTimeOffset.UtcNow);
        var catalog = JsonSerializer.Deserialize(payload, TrustJsonContext.Default.CatalogDocument) ?? throw new TrustException("Empty catalog.");
        Freshness.Check("catalog", catalog.Sequence, catalog.Expires, Path.Combine(_ctx.Paths.StateDir, "sequences.json"), DateTimeOffset.UtcNow, persist);
        foreach (var model in catalog.Models)
        {
            if (!LocalAIPaths.IsSafeName(model.Id) || !LocalAIPaths.IsSafeName(model.Version))
            {
                throw new TrustException($"Catalog entry '{model.Id}' has an invalid id or version.");
            }
        }

        return catalog;
    }
}
