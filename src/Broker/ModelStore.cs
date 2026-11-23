using System.Runtime.InteropServices;
using System.Text.Json;
using CommunityToolkit.AI.Protocol;
using CommunityToolkit.AI.Protocol.Runtime;
using CommunityToolkit.AI.Protocol.Trust;

namespace CommunityToolkit.AI.Broker;

/// <summary>
/// The per-user model store: models\&lt;id&gt;\&lt;version&gt;\ folders built from a hash-addressed blob store
/// (models\.blobs\&lt;sha256&gt;) so identical files are stored once. Writes are staged, verified, then renamed into place.
/// </summary>
internal sealed class ModelStore
{
    private readonly BrokerContext _ctx;
    private readonly CatalogService _catalog;
    private readonly Dictionary<string, InstallJob> _jobs = new(StringComparer.Ordinal);
    private readonly object _jobsLock = new();

    public ModelStore(BrokerContext ctx, CatalogService catalog)
    {
        _ctx = ctx;
        _catalog = catalog;
    }

    private LocalAIPaths Paths => _ctx.Paths;

    public bool IsDownloading(string id) { lock (_jobsLock) { return _jobs.Keys.Any(k => k.StartsWith(id + "/", StringComparison.Ordinal)); } }

    /// <summary>Returns the installed model if its folder, model.json and file sizes check out.</summary>
    public InstalledModel? GetInstalled(string id, string version)
    {
        string dir = Paths.ModelDir(id, version);
        string manifest = Path.Combine(dir, "model.json");
        try
        {
            if (!File.Exists(manifest))
            {
                return null;
            }

            var installed = JsonSerializer.Deserialize(File.ReadAllBytes(manifest), TrustJsonContext.Default.InstalledModel);
            if (installed is null || installed.Id != id || installed.Version != version)
            {
                return null;
            }

            foreach (var file in installed.Files)
            {
                var info = new FileInfo(ResolveModelFile(dir, file.Path));
                if (!info.Exists || info.Length != file.Size)
                {
                    return null;
                }
            }

            return installed;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The newest installed version of a model, used when the catalog cannot be reached.</summary>
    public InstalledModel? FindNewestInstalled(string id)
    {
        string root = Path.Combine(Paths.ModelsDir, id);
        if (!LocalAIPaths.IsSafeName(id) || !Directory.Exists(root))
        {
            return null;
        }

        return Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(LocalAIPaths.IsSafeName)
            .OrderByDescending(v => v, Comparer<string?>.Create(SemVer.Compare))
            .Select(v => GetInstalled(id, v!))
            .FirstOrDefault(m => m is not null);
    }

    /// <summary>Bytes still to download for a catalog model.</summary>
    public long RemainingBytes(CatalogModel model)
    {
        if (GetInstalled(model.Id, model.Version) is not null)
        {
            return 0;
        }

        long remaining = 0;
        foreach (var file in model.Files)
        {
            if (File.Exists(BlobPath(file.Sha256)))
            {
                continue;
            }

            string partial = PartialPath(file.Sha256);
            long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            remaining += Math.Max(0, file.Size - have);
        }

        return remaining;
    }

    public void TouchLastUsed(InstalledModel model)
    {
        model.LastUsedAt = DateTimeOffset.UtcNow;
        try
        {
            AtomicFile.WriteAllBytes(Path.Combine(Paths.ModelDir(model.Id, model.Version), "model.json"), JsonSerializer.SerializeToUtf8Bytes(model, TrustJsonContext.Default.InstalledModel));
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Installs a model. Concurrent callers for the same model share one download and all receive its progress.
    /// </summary>
    public async Task<InstalledModel> InstallAsync(CatalogModel model, Action<long, long> progress, CancellationToken ct)
    {
        if (GetInstalled(model.Id, model.Version) is { } installed)
        {
            return installed;
        }

        string key = $"{model.Id}/{model.Version}";
        InstallJob job;
        lock (_jobsLock)
        {
            if (!_jobs.TryGetValue(key, out job!) || job.Task.IsCompleted)
            {
                job = new InstallJob(model.TotalSize);
                _jobs[key] = job;
                job.Task = Task.Run(() => RunInstallAsync(model, job), job.Cancellation.Token);
                _ = job.Task.ContinueWith(_ => { lock (_jobsLock) { if (_jobs.TryGetValue(key, out var current) && current == job) { _jobs.Remove(key); } } }, TaskScheduler.Default);
            }

            job.Waiters++;
            job.Progress += progress;
        }

        try
        {
            progress(job.Done, job.Total);
            return await job.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_jobsLock)
            {
                job.Progress -= progress;
                // When every waiter has given up, stop the download. The partial files stay for resume.
                if (--job.Waiters == 0 && !job.Task.IsCompleted)
                {
                    job.Cancellation.Cancel();
                }
            }
        }
    }

    private async Task<InstalledModel> RunInstallAsync(CatalogModel model, InstallJob job)
    {
        var started = DateTimeOffset.UtcNow;
        var ct = job.Cancellation.Token;
        Directory.CreateDirectory(Paths.StagingDir);
        Directory.CreateDirectory(Paths.BlobsDir);
        DiskSpace.EnsureAvailable(Paths.ModelsDir, RemainingBytes(model));

        var hosts = _catalog.DownloadHosts;
        var downloader = new SecureDownloader(_ctx.Http, uri => SecureDownloader.HostMatches(uri.Host, hosts), allowFileUrls: _ctx.DevMode);
        long completed = 0;
        try
        {
            foreach (var file in model.Files)
            {
                ValidateRelativePath(file.Path);
                if (file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                {
                    throw new RpcException(ErrorCodes.IntegrityFailure, $"Catalog entry for {file.Path} has an invalid SHA-256.");
                }

                string blob = BlobPath(file.Sha256);
                long before = completed;
                if (!File.Exists(blob))
                {
                    await downloader.DownloadAsync(_catalog.ResolveFileUrl(file), blob, file.Size, file.Sha256, PartialPath(file.Sha256), done => job.Report(before + done), ct).ConfigureAwait(false);
                }

                completed += file.Size;
                job.Report(completed);
            }

            // Assemble and verify the folder in staging, then rename it into place.
            string finalDir = Paths.ModelDir(model.Id, model.Version);
            string stagingDir = Path.Combine(Paths.ModelsDir, model.Id, $".{model.Version}.{Guid.NewGuid():N}.tmp");
            Directory.CreateDirectory(stagingDir);
            try
            {
                foreach (var file in model.Files)
                {
                    string blob = BlobPath(file.Sha256);
                    if (new FileInfo(blob).Length != file.Size || SecureDownloader.Sha256OfFile(blob) != file.Sha256.ToLowerInvariant())
                    {
                        RuntimeInstaller.TryDeleteFile(blob);
                        throw new RpcException(ErrorCodes.IntegrityFailure, $"Stored file for {file.Path} failed verification and was removed.");
                    }

                    string target = ResolveModelFile(stagingDir, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    LinkOrCopy(blob, target);
                }

                var installed = new InstalledModel
                {
                    Id = model.Id,
                    Version = model.Version,
                    Task = model.Task,
                    Adapter = model.Adapter,
                    Files = model.Files,
                    InstalledAt = DateTimeOffset.UtcNow,
                    LastUsedAt = DateTimeOffset.UtcNow,
                };
                File.WriteAllBytes(Path.Combine(stagingDir, "model.json"), JsonSerializer.SerializeToUtf8Bytes(installed, TrustJsonContext.Default.InstalledModel));
                if (Directory.Exists(finalDir))
                {
                    Directory.Delete(finalDir, recursive: true);
                }

                Directory.Move(stagingDir, finalDir);
                Telemetry.Record("model.install", ("model", model.Id), ("version", model.Version), ("result", "ok"), ("latency", Telemetry.LatencyBucket(DateTimeOffset.UtcNow - started)));
                Log.Info($"Installed {model.Id} {model.Version}.");
                return installed;
            }
            finally
            {
                RuntimeInstaller.TryDeleteDirectory(stagingDir);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Telemetry.Record("model.install", ("model", model.Id), ("version", model.Version), ("result", ex is RpcException r ? r.Code : ex.GetType().Name));
            throw;
        }
    }

    /// <summary>Removes model versions that are not kept and blobs no installed model references.</summary>
    public void CollectGarbage(ISet<string> keepVersionKeys, ISet<string> inUseKeys)
    {
        if (!Directory.Exists(Paths.ModelsDir))
        {
            return;
        }

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var idDir in Directory.EnumerateDirectories(Paths.ModelsDir))
        {
            string id = Path.GetFileName(idDir);
            if (id == ".blobs")
            {
                continue;
            }

            foreach (var versionDir in Directory.EnumerateDirectories(idDir))
            {
                string version = Path.GetFileName(versionDir);
                string key = $"{id}/{version}";
                var installed = GetInstalled(id, version);
                bool keep = installed is not null && (keepVersionKeys.Contains(key) || inUseKeys.Contains(key));
                if (!keep && !IsDownloading(id))
                {
                    Log.Info($"Garbage collecting {key}.");
                    RuntimeInstaller.TryDeleteDirectory(versionDir);
                    continue;
                }

                foreach (var file in installed?.Files ?? [])
                {
                    referenced.Add(file.Sha256.ToLowerInvariant());
                }
            }
        }

        if (Directory.Exists(Paths.BlobsDir))
        {
            foreach (var blob in Directory.EnumerateFiles(Paths.BlobsDir))
            {
                if (!referenced.Contains(Path.GetFileName(blob)))
                {
                    RuntimeInstaller.TryDeleteFile(blob);
                }
            }
        }
    }

    private string BlobPath(string sha256) => Path.Combine(Paths.BlobsDir, sha256.ToLowerInvariant());

    private string PartialPath(string sha256) => Path.Combine(Paths.StagingDir, sha256.ToLowerInvariant() + ".partial");

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') || path.Replace('\\', '/').Split('/').Any(s => s is "" or "." or ".."))
        {
            throw new RpcException(ErrorCodes.IntegrityFailure, $"Catalog file path '{path}' is not allowed.");
        }
    }

    private static string ResolveModelFile(string dir, string relative)
    {
        ValidateRelativePath(relative);
        string root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : throw new InvalidDataException($"'{relative}' escapes the model folder.");
    }

    private static void LinkOrCopy(string source, string target)
    {
        if (File.Exists(target))
        {
            File.Delete(target);
        }

        if (!CreateHardLinkW(target, source, IntPtr.Zero))
        {
            File.Copy(source, target);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    private sealed class InstallJob
    {
        public InstallJob(long total) => Total = total;

        public Task<InstalledModel> Task { get; set; } = null!;

        public CancellationTokenSource Cancellation { get; } = new();

        public int Waiters { get; set; }

        public long Total { get; }

        public long Done { get; private set; }

        public event Action<long, long>? Progress;

        public void Report(long done)
        {
            Done = done;
            Progress?.Invoke(done, Total);
        }
    }
}
