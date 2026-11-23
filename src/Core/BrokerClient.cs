using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CommunityToolkit.AI.Protocol;

namespace CommunityToolkit.AI;

/// <summary>One shared broker connection per process (and per install home).</summary>
internal sealed class BrokerClient
{
    private static readonly ConcurrentDictionary<string, BrokerClient> s_instances = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> s_taskContracts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RpcConnection? _connection;

    private BrokerClient(ClientEnvironment env)
    {
        Environment = env;
        Bootstrapper = new Bootstrapper(env);
    }

    public ClientEnvironment Environment { get; }

    public Bootstrapper Bootstrapper { get; }

    public HelloResponse? Hello { get; private set; }

    public static BrokerClient Get()
    {
        var env = LocalAIRuntime.ResolveEnvironment();
        string key = string.Join("|", env.Paths.Home, env.Config.RootUrl, env.Config.Channel, env.DevMode, string.Join(",", env.Config.DevTrustKeys));
        return s_instances.GetOrAdd(key, _ => new BrokerClient(env));
    }

    /// <summary>Records a task contract this process speaks, so the next handshake advertises it.</summary>
    public static void RegisterTaskContract(string task, string maxVersion) => s_taskContracts[task] = maxVersion;

    public bool IsConnected => _connection?.IsConnected == true;

    public async Task<RpcConnection> GetConnectionAsync(bool allowInstall, Action<long, long>? runtimeProgress, CancellationToken ct)
    {
        if (_connection is { IsConnected: true } existing)
        {
            return existing;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is { IsConnected: true } raced)
            {
                return raced;
            }

            Stream stream = await Bootstrapper.ConnectAsync(allowInstall, runtimeProgress, ct).ConfigureAwait(false);
            var connection = new RpcConnection(stream);
            connection.Start();
            try
            {
                var hello = new HelloRequest
                {
                    ClientName = LocalAIRuntime.ClientName ?? Process.GetCurrentProcess().ProcessName,
                    ProcessId = System.Environment.ProcessId,
                    Tasks = new Dictionary<string, string>(s_taskContracts),
                };
                var result = await connection.InvokeAsync(CoreMethods.Hello, JsonSerializer.SerializeToElement(hello, CoreJsonContext.Default.HelloRequest), ct).ConfigureAwait(false);
                Hello = result?.Deserialize(CoreJsonContext.Default.HelloResponse);
                foreach (var deprecated in Hello?.Deprecated ?? [])
                {
                    Trace.TraceWarning($"LocalAI: task contract {deprecated} is deprecated. Update the matching CommunityToolkit.AI package.");
                }
            }
            catch (Exception ex)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw LocalAiException.FromRpc(ex, fromHost: false);
            }

            _connection = connection;
            return connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Calls the broker, reconnecting once if the broker went away (for example after a self-update).</summary>
    public async Task<TResult> CallAsync<TRequest, TResult>(string method, TRequest request, JsonTypeInfo<TRequest> requestInfo, JsonTypeInfo<TResult> resultInfo, bool allowInstall, CancellationToken ct)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(request, requestInfo);
        for (int attempt = 0; ; attempt++)
        {
            var connection = await GetConnectionAsync(allowInstall, null, ct).ConfigureAwait(false);
            try
            {
                var result = await connection.InvokeAsync(method, payload, ct).ConfigureAwait(false);
                return result is { } element ? element.Deserialize(resultInfo)! : throw new LocalAiException(LocalAiErrorCode.Unknown, $"{method} returned no result.");
            }
            catch (RpcConnectionLostException) when (attempt == 0)
            {
            }
            catch (Exception ex)
            {
                throw LocalAiException.FromRpc(ex, fromHost: false);
            }
        }
    }
}
