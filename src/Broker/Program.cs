using System.IO.Pipes;
using System.Text.Json;
using CommunityToolkit.AI.Broker;
using CommunityToolkit.AI.Protocol;
using CommunityToolkit.AI.Protocol.Runtime;
using CommunityToolkit.AI.Protocol.Trust;

var options = BrokerArgs.Parse(args);
var paths = new LocalAIPaths(options.Home);
Directory.CreateDirectory(paths.LogsDir);
Log.Initialize(paths.LogsDir, options.Mode == "serve" ? "broker" : options.Mode);

try
{
    return options.Mode switch
    {
        "health-check" => HealthCheck(),
        "update-once" => await UpdateOnceAsync(paths),
        "uninstall" => await UninstallAsync(paths),
        _ => await ServeAsync(paths),
    };
}
catch (Exception ex)
{
    Log.Error("Fatal", ex);
    return BrokerExitCodes.Error;
}

static int HealthCheck()
{
    string dir = AppContext.BaseDirectory;
    bool filesOk = !File.Exists(Path.Combine(dir, "files.json")) || RuntimeInstaller.VerifyRuntimeDirectory(dir, null, fullHash: true);
    bool hostOk = File.Exists(Path.Combine(dir, "host.exe"));
    Log.Info($"Health check: files {(filesOk ? "ok" : "bad")}, host {(hostOk ? "ok" : "missing")}.");
    return filesOk && hostOk ? 0 : BrokerExitCodes.HealthCheckFailed;
}

static async Task<int> UpdateOnceAsync(LocalAIPaths paths)
{
    using var ctx = new BrokerContext(paths);
    Telemetry.Initialize(paths.LogsDir, ctx.Policy.DisableTelemetry);
    var updater = new Updater(ctx, new CatalogService(ctx));
    await updater.CheckAsync(CancellationToken.None);
    return 0;
}

static async Task<int> UninstallAsync(LocalAIPaths paths)
{
    // Ask a running broker to stop (it takes its hosts with it).
    using (var pipe = new NamedPipeClientStream(".", paths.BrokerPipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
    {
        try
        {
            await pipe.ConnectAsync(1000);
            await using var rpc = new RpcConnection(pipe);
            rpc.Start();
            await rpc.InvokeAsync(CoreMethods.Hello, JsonSerializer.SerializeToElement(new HelloRequest { ClientName = "uninstall", ProcessId = Environment.ProcessId }, CoreJsonContext.Default.HelloRequest));
            await rpc.InvokeAsync(CoreMethods.Shutdown, null);
            await Task.Delay(1500);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or RpcException)
        {
        }
    }

    SystemIntegration.Unregister();
    foreach (var dir in new[] { paths.ModelsDir, paths.EpsDir, paths.StagingDir, paths.StateDir })
    {
        RuntimeInstaller.TryDeleteDirectory(dir);
    }

    // This executable lives under runtime\, so remove the rest of the home after we exit.
    // cmd.exe does not understand \" escaping, so the command goes into a script rather than onto the command line.
    string script = Path.Combine(Path.GetTempPath(), $"localai-uninstall-{Guid.NewGuid():N}.cmd");
    File.WriteAllText(script, $"@echo off\r\nping -n 4 127.0.0.1 >nul\r\nrmdir /s /q \"{paths.Home}\"\r\ndel \"%~f0\"\r\n");
    ProcessLauncher.Start(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", script], Path.GetTempPath(), breakawayFromJob: true).Dispose();
    Console.WriteLine($"Local AI runtime removed from {paths.Home}.");
    return 0;
}

static async Task<int> ServeAsync(LocalAIPaths paths)
{
    using var singleton = FileLock.TryAcquire(Path.Combine(paths.StateDir, "broker.lock"));
    if (singleton is null)
    {
        Log.Info("Another broker is already running.");
        return BrokerExitCodes.AlreadyRunning;
    }

    using var ctx = new BrokerContext(paths);
    Telemetry.Initialize(paths.LogsDir, ctx.Policy.DisableTelemetry);
    var catalog = new CatalogService(ctx);
    var store = new ModelStore(ctx, catalog);
    using var hosts = new HostManager(ctx);
    var server = new BrokerServer(ctx, catalog, store, hosts);
    var updater = new Updater(ctx, catalog);
    var pipeServer = new PipeServer(paths.BrokerPipeName, server.OnConnect, server.OnDisconnect);

    // A draining older broker may still hold pipe instances for a few seconds.
    NamedPipeServerStream? first = null;
    for (int attempt = 0; first is null; attempt++)
    {
        try
        {
            first = pipeServer.CreateFirstInstance();
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 120)
        {
            await Task.Delay(250);
        }
    }

    bool installedRuntime = string.Equals(
        Path.GetFullPath(ctx.RuntimeDir).TrimEnd('\\'),
        Path.GetFullPath(paths.RuntimeVersionDir(ctx.RuntimeVersion)).TrimEnd('\\'),
        StringComparison.OrdinalIgnoreCase);
    Log.Info($"Broker {ctx.RuntimeVersion} serving {paths.BrokerPipeName} (home {paths.Home}, channel {ctx.Config.Channel}, dev {ctx.DevMode}, installed {installedRuntime}).");
    Telemetry.Record("broker.start", ("version", ctx.RuntimeVersion));

    using var stop = new CancellationTokenSource();
    using var stopAccepting = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
    server.ShutdownRequested += () => stop.Cancel();
    server.UpdateNeeded += () => _ = updater.CheckAsync(stop.Token);
    var serving = pipeServer.RunAsync(first, stopAccepting.Token);

    if (installedRuntime)
    {
        if (!ctx.DevMode && paths.IsDefaultHome)
        {
            _ = Task.Run(() => SystemIntegration.Register(paths, ctx.RuntimeVersion));
        }

        _ = Task.Run(() => updater.RunAsync(stop.Token));
        _ = Task.Run(() => updater.CleanupOldRuntimes(TimeSpan.FromDays(7)));
    }

    _ = Task.Run(async () =>
    {
        // Collect superseded model versions, keeping whatever the catalog currently lists.
        try
        {
            var current = await catalog.GetCatalogAsync(false, stop.Token);
            if (current is not null)
            {
                var keep = current.Models.Select(m => $"{m.Id}/{m.Version}").ToHashSet();
                var inUse = hosts.Hosts.Select(h => $"{h.ModelId}/{h.ModelVersion}").ToHashSet();
                store.CollectGarbage(keep, inUse);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Model garbage collection failed: {ex.Message}");
        }
    });

    TimeSpan idleTimeout = TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("LOCALAI_BROKER_IDLE_SECONDS"), out double idle) ? idle : 600);
    TimeSpan tick = TimeSpan.FromMilliseconds(int.TryParse(Environment.GetEnvironmentVariable("LOCALAI_BROKER_TICK_MS"), out int t) ? t : 5000);
    bool draining = false;
    var drainStarted = DateTimeOffset.MinValue;
    var singletonHandle = singleton;

    while (!stop.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(tick, stop.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }

        hosts.EvictUnderMemoryPressure();

        if (!draining && installedRuntime && paths.ReadCurrentVersion() is { } current && current != ctx.RuntimeVersion &&
            RuntimeInstaller.VerifyRuntimeDirectory(paths.RuntimeVersionDir(current), current, fullHash: false))
        {
            // A newer runtime was installed: stop taking clients so the next connection starts the new broker.
            Log.Info($"Runtime {current} is now current; draining broker {ctx.RuntimeVersion}.");
            draining = true;
            drainStarted = DateTimeOffset.UtcNow;
            stopAccepting.Cancel();
            singletonHandle.Dispose();
        }

        if (draining)
        {
            await server.CloseIdleClientsAsync();
            // Existing hosts keep serving their sessions until they idle out; the job object would kill them if we left first.
            if ((server.ClientCount == 0 && hosts.Count == 0) || DateTimeOffset.UtcNow - drainStarted > TimeSpan.FromHours(1))
            {
                break;
            }

            continue;
        }

        if (server.ClientCount == 0 && !server.HasInFlightCalls && DateTimeOffset.UtcNow - server.LastActivity > idleTimeout)
        {
            Log.Info("No clients; idling out.");
            break;
        }
    }

    stopAccepting.Cancel();
    stop.Cancel();
    try
    {
        await serving;
    }
    catch (OperationCanceledException)
    {
    }

    Log.Info("Broker exiting.");
    return 0;
}

internal sealed record BrokerArgs(string Mode, string? Home)
{
    public static BrokerArgs Parse(string[] args)
    {
        string mode = "serve";
        string? home = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--home" when i + 1 < args.Length:
                    home = args[++i];
                    break;
                case "--health-check":
                    mode = "health-check";
                    break;
                case "--update-once":
                    mode = "update-once";
                    break;
                case "--uninstall":
                    mode = "uninstall";
                    break;
            }
        }

        return new BrokerArgs(mode, home);
    }
}
