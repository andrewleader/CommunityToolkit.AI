using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.AI.Protocol;
using CommunityToolkit.AI.Protocol.Runtime;
using CommunityToolkit.AI.Protocol.Trust;

namespace CommunityToolkit.AI.Broker;

/// <summary>
/// One host process per (model id, version, EP, runtime version), shared by every app. Hosts run in a kill-on-close job object
/// with memory and CPU limits, evict themselves when idle, and are restarted lazily after a crash.
/// </summary>
internal sealed class HostManager : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(5);
    private readonly BrokerContext _ctx;
    private readonly ConcurrentDictionary<string, HostEntry> _hosts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly Lazy<JobObject?> _job;

    public HostManager(BrokerContext ctx)
    {
        _ctx = ctx;
        _job = new Lazy<JobObject?>(CreateJob);
    }

    public int Count => _hosts.Values.Count(h => !h.HasExited);

    public IReadOnlyCollection<HostEntry> Hosts => _hosts.Values.Where(h => !h.HasExited).ToList();

    public async Task<HostEntry> GetOrStartAsync(InstalledModel model, string ep, int? failedProcessId, CancellationToken ct)
    {
        string key = HostKey(model.Id, model.Version, ep);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_hosts.TryGetValue(key, out var existing) && !existing.HasExited)
            {
                if (existing.Process.Id != failedProcessId)
                {
                    existing.LastSessionAt = DateTimeOffset.UtcNow;
                    return existing;
                }

                // A client lost this host, but it has not finished exiting (for example while crash reporting runs).
                Log.Warn($"Host {existing.Process.Id} for {model.Id} was reported failed; replacing it.");
                existing.Kill();
                existing.Process.WaitForExit(5000);
            }

            var entry = await StartAsync(key, model, ep, ct).ConfigureAwait(false);
            _hosts[key] = entry;
            return entry;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<HostEntry> StartAsync(string key, InstalledModel model, string ep, CancellationToken ct)
    {
        string hostExe = ResolveHostExe();
        string pipe = _ctx.Paths.HostPipeName(key);
        string readyEvent = $"Local\\LocalAI-ready-{key}-{Guid.NewGuid():N}";
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyEvent);
        string adapterParams = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(model.Adapter.Parameters, RuntimeJsonContext.Default.DictionaryStringString));
        var args = new List<string>
        {
            "--pipe", pipe,
            "--ready-event", readyEvent,
            "--home", _ctx.Paths.Home,
            "--model-dir", _ctx.Paths.ModelDir(model.Id, model.Version),
            "--model-id", model.Id,
            "--model-version", model.Version,
            "--task", model.Task,
            "--adapter", model.Adapter.Id,
            "--adapter-params", adapterParams,
            "--ep", ep,
            "--broker-pid", Environment.ProcessId.ToString(),
        };
        if (Environment.GetEnvironmentVariable("LOCALAI_HOST_IDLE_SECONDS") is { Length: > 0 } idle)
        {
            args.Add("--idle-seconds");
            args.Add(idle);
        }

        var started = Stopwatch.StartNew();
        var job = _job.Value;
        var process = ProcessLauncher.Start(hostExe, args, Path.GetDirectoryName(hostExe)!, breakawayFromJob: false, beforeResume: h => job?.Assign(h));
        Log.Info($"Started host {process.Id} for {model.Id} {model.Version} on {ep}.");

        var readyTask = Task.Run(() => ready.WaitOne(StartTimeout), ct);
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(readyTask, exitTask).ConfigureAwait(false);
        if (completed == exitTask || !await readyTask.ConfigureAwait(false))
        {
            int? code = process.HasExited ? process.ExitCode : null;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            Telemetry.Record("host.start", ("model", model.Id), ("ep", ep), ("result", "failed"), ("exitCode", code?.ToString()));
            throw new RpcException(code == HostExitCodes.UnsupportedAdapter ? ErrorCodes.NotSupported : ErrorCodes.HostCrashed,
                $"The model host for '{model.Id}' failed to start (exit code {code?.ToString() ?? "timeout"}). See {_ctx.Paths.LogsDir}.");
        }

        Telemetry.Record("host.start", ("model", model.Id), ("ep", ep), ("result", "ok"), ("latency", Telemetry.LatencyBucket(started.Elapsed)));
        var entry = new HostEntry(key, process, pipe, model.Id, model.Version, ep);
        _ = process.WaitForExitAsync().ContinueWith(_ =>
        {
            int exitCode = process.ExitCode;
            if (exitCode != 0)
            {
                Telemetry.Record("host.crash", ("model", model.Id), ("ep", ep), ("exitCode", exitCode.ToString()));
                Log.Warn($"Host {process.Id} for {model.Id} exited with code {exitCode}.");
            }
            else
            {
                Log.Info($"Host {process.Id} for {model.Id} exited.");
            }

            _hosts.TryRemove(new KeyValuePair<string, HostEntry>(key, entry));
        }, TaskScheduler.Default);
        return entry;
    }

    /// <summary>Under memory pressure, stops the host that has gone longest without a new session.</summary>
    public void EvictUnderMemoryPressure()
    {
        var (total, available) = MemoryInfo.Get();
        if (total == 0 || available > total / 10)
        {
            return;
        }

        var victim = _hosts.Values.Where(h => !h.HasExited && DateTimeOffset.UtcNow - h.LastSessionAt > TimeSpan.FromMinutes(1)).MinBy(h => h.LastSessionAt);
        if (victim is not null)
        {
            Log.Warn($"Memory pressure ({available / (1024 * 1024)} MB free): evicting host for {victim.ModelId}.");
            Telemetry.Record("host.evict", ("model", victim.ModelId), ("reason", "memory"));
            victim.Kill();
        }
    }

    public void StopAll()
    {
        foreach (var host in _hosts.Values)
        {
            host.Kill();
        }
    }

    private string ResolveHostExe()
    {
        if (_ctx.DevMode && Environment.GetEnvironmentVariable("LOCALAI_DEV_HOST_PATH") is { Length: > 0 } devPath)
        {
            return devPath;
        }

        return Path.Combine(_ctx.RuntimeDir, "host.exe");
    }

    private string HostKey(string id, string version, string ep) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{id}|{version}|{ep}|{_ctx.RuntimeVersion}"))).Substring(0, 16).ToLowerInvariant();

    private static JobObject? CreateJob()
    {
        try
        {
            var (total, _) = MemoryInfo.Get();
            ulong limit = total > 0 ? total / 100 * 85 : 0;
            if (ulong.TryParse(Environment.GetEnvironmentVariable("LOCALAI_HOST_MEMORY_MB"), out ulong mb))
            {
                limit = mb * 1024 * 1024;
            }

            int cpu = int.TryParse(Environment.GetEnvironmentVariable("LOCALAI_HOST_CPU_PERCENT"), out int c) ? c : 0;
            return new JobObject(limit, cpu);
        }
        catch (InvalidOperationException ex)
        {
            Log.Warn($"Could not create the host job object: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        StopAll();
        if (_job.IsValueCreated)
        {
            _job.Value?.Dispose();
        }
    }

    internal sealed class HostEntry
    {
        public HostEntry(string key, Process process, string pipe, string modelId, string modelVersion, string ep)
        {
            Key = key;
            Process = process;
            Pipe = pipe;
            ModelId = modelId;
            ModelVersion = modelVersion;
            Ep = ep;
        }

        public string Key { get; }

        public Process Process { get; }

        public string Pipe { get; }

        public string ModelId { get; }

        public string ModelVersion { get; }

        public string Ep { get; }

        public DateTimeOffset LastSessionAt { get; set; } = DateTimeOffset.UtcNow;

        public bool HasExited
        {
            get
            {
                try
                {
                    return Process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public void Kill()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }
}
