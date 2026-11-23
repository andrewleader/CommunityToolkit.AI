using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.AI.Host;
using CommunityToolkit.AI.Host.Adapters;
using CommunityToolkit.AI.Protocol;
using CommunityToolkit.AI.Protocol.Runtime;

// host.exe: loads one model with one task adapter and serves it to every app that opens a session (spec 4.3).
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (int i = 0; i + 1 < args.Length; i += 2)
{
    options[args[i].TrimStart('-')] = args[i + 1];
}

string Arg(string name) => options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"--{name} is required.");

ModelInfo model;
string pipeName;
try
{
    var paths = new LocalAIPaths(options.GetValueOrDefault("home"));
    Log.Initialize(paths.LogsDir, "host");
    pipeName = Arg("pipe");
    var parameters = options.TryGetValue("adapter-params", out var encoded)
        ? JsonSerializer.Deserialize(Convert.FromBase64String(encoded), RuntimeJsonContext.Default.DictionaryStringString) ?? new()
        : new Dictionary<string, string>();
    model = new ModelInfo(Arg("model-id"), Arg("model-version"), Arg("model-dir"), options.GetValueOrDefault("ep", "cpu"), parameters);
}
catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException)
{
    Console.Error.WriteLine(ex.Message);
    return HostExitCodes.BadArguments;
}

string adapterId = options.GetValueOrDefault("adapter", "");
Log.Info($"Loading {model.Id} {model.Version} with adapter {adapterId} on {model.Ep}.");
var loadTimer = Stopwatch.StartNew();
ITaskAdapter? adapter;
try
{
    adapter = AdapterRegistry.Create(adapterId, model);
}
catch (Exception ex)
{
    Log.Error($"Model load failed for {model.Id}", ex);
    return HostExitCodes.ModelLoadFailed;
}

if (adapter is null || adapter.Task != options.GetValueOrDefault("task"))
{
    Log.Error($"Adapter '{adapterId}' is not available for task '{options.GetValueOrDefault("task")}'.");
    return HostExitCodes.UnsupportedAdapter;
}

Log.Info($"Loaded {model.Id} in {loadTimer.ElapsedMilliseconds} ms.");
using (adapter)
{
    var server = new HostServer(model, adapter);
    var pipeServer = new PipeServer(pipeName, server.OnConnect, server.OnDisconnect);
    var first = pipeServer.CreateFirstInstance();
    using var stop = new CancellationTokenSource();
    var serving = pipeServer.RunAsync(first, stop.Token);

    if (options.TryGetValue("ready-event", out var readyName) && EventWaitHandle.TryOpenExisting(readyName, out var ready))
    {
        using (ready)
        {
            ready.Set();
        }
    }

    // Idle eviction: exit after a period without requests. Connected clients reconnect lazily on their next call.
    TimeSpan idle = TimeSpan.FromSeconds(options.TryGetValue("idle-seconds", out var s) && double.TryParse(s, out double seconds) ? seconds : 300);
    int brokerPid = options.TryGetValue("broker-pid", out var b) && int.TryParse(b, out int pid) ? pid : 0;
    using var broker = brokerPid > 0 ? TryGetProcess(brokerPid) : null;
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, Math.Max(0.25, idle.TotalSeconds / 4))));
        if (!server.Busy && DateTimeOffset.UtcNow - server.LastActivity > idle)
        {
            Log.Info($"Idle for {idle}; exiting.");
            break;
        }

        // The job object normally takes care of this; it is a fallback when the host could not be put in a job.
        if (broker is not null && broker.HasExited)
        {
            Log.Info("Broker exited; exiting.");
            break;
        }
    }

    stop.Cancel();
    try
    {
        await serving;
    }
    catch (OperationCanceledException)
    {
    }
}

return HostExitCodes.Ok;

static Process? TryGetProcess(int pid)
{
    try
    {
        return Process.GetProcessById(pid);
    }
    catch (ArgumentException)
    {
        return null;
    }
}
