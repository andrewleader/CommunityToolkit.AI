using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CommunityToolkit.AI.Protocol;

namespace CommunityToolkit.AI;

/// <summary>
/// A session-bound connection from a task package to the shared model host. Task packages use it to send
/// their own contract's messages; apps use the typed task clients instead.
/// If the host crashes, in-flight calls raise <see cref="HostCrashedException"/> and the next call restarts the host.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public sealed class ModelHostConnection : IAsyncDisposable
{
    private readonly Model _model;
    private readonly string _contractMaxVersion;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RpcConnection? _connection;
    private int? _hostProcessId;
    private int? _lostHostProcessId;
    private bool _disposed;

    internal ModelHostConnection(Model model, string contractMaxVersion)
    {
        _model = model;
        _contractMaxVersion = contractMaxVersion;
    }

    public string ModelId => _model.Id;

    /// <summary>The model version the host loaded.</summary>
    public string ModelVersion { get; private set; } = "";

    /// <summary>The execution provider the host runs on, for example cpu.</summary>
    public string ExecutionProvider { get; private set; } = "";

    /// <summary>The negotiated task contract version.</summary>
    public string ContractVersion { get; private set; } = "";

    public bool IsConnected => _connection?.IsConnected == true;

    /// <summary>Sends a unary request and returns the result payload.</summary>
    public async Task<JsonElement?> InvokeAsync(string method, JsonElement? payload, CancellationToken ct = default)
    {
        await using var call = await StartAsync(method, payload, initialCredits: null, ct).ConfigureAwait(false);
        try
        {
            return await call.GetResultAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw LocalAiException.FromRpc(ex, fromHost: true);
        }
    }

    /// <summary>Sends a streaming request. Events flow with credit-based backpressure.</summary>
    public async Task<ModelHostStream> StreamAsync(string method, JsonElement? payload, CancellationToken ct = default) =>
        new(await StartAsync(method, payload, initialCredits: 32, ct).ConfigureAwait(false));

    private async Task<RpcOutgoingCall> StartAsync(string method, JsonElement? payload, int? initialCredits, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var connection = await GetConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                return await connection.StartCallAsync(method, payload, initialCredits, ct).ConfigureAwait(false);
            }
            catch (RpcConnectionLostException) when (attempt == 0)
            {
                // The request was never delivered, so it is safe to reconnect (restarting the host if needed) and resend.
            }
            catch (Exception ex)
            {
                throw LocalAiException.FromRpc(ex, fromHost: true);
            }
        }
    }

    private async Task<RpcConnection> GetConnectionAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is { IsConnected: true } connection)
        {
            return connection;
        }

        await ReconnectAsync(ct).ConfigureAwait(false);
        return _connection!;
    }

    internal async Task ReconnectAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is { IsConnected: true })
            {
                return;
            }

            if (_connection is not null)
            {
                // The host went away (crash or idle eviction); make sure the broker does not hand it out again.
                _lostHostProcessId = _hostProcessId;
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }

            var broker = BrokerClient.Get();
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    _connection = await ConnectOnceAsync(broker, ct).ConfigureAwait(false);
                    return;
                }
                catch (HostCrashedException) when (attempt < 4)
                {
                    await Task.Delay(200 * attempt, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RpcConnection> ConnectOnceAsync(BrokerClient broker, CancellationToken ct)
    {
            var session = await broker.CallAsync(
                CoreMethods.OpenSession,
                new OpenSessionRequest
                {
                    ModelId = _model.Id,
                    Task = _model.TaskId,
                    Device = _model.Options.Device.ToString(),
                    ContractVersion = _contractMaxVersion,
                    FailedHostProcessId = _lostHostProcessId,
                },
                CoreJsonContext.Default.OpenSessionRequest,
                CoreJsonContext.Default.OpenSessionResponse,
                allowInstall: false,
                ct).ConfigureAwait(false);

            // Until this handshake succeeds, a retry should also report this host as failed.
            _lostHostProcessId = session.HostProcessId;
            var pipe = new NamedPipeClientStream(".", session.HostPipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(10_000, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw new HostCrashedException("Could not connect to the model host.", ex);
            }

            var rpc = new RpcConnection(pipe);
            rpc.Start();
            try
            {
                var hello = new HostHelloRequest { Task = _model.TaskId, ContractVersion = _contractMaxVersion, ClientProcessId = Environment.ProcessId };
                var result = await rpc.InvokeAsync(CoreMethods.HostHello, JsonSerializer.SerializeToElement(hello, CoreJsonContext.Default.HostHelloRequest), ct).ConfigureAwait(false);
                var response = result?.Deserialize(CoreJsonContext.Default.HostHelloResponse) ?? throw new HostCrashedException("The model host did not complete the handshake.");
                ModelVersion = response.ModelVersion;
                ExecutionProvider = response.Ep;
                ContractVersion = response.ContractVersion;
            }
            catch (Exception ex)
            {
                await rpc.DisposeAsync().ConfigureAwait(false);
                throw LocalAiException.FromRpc(ex, fromHost: true);
            }

            _hostProcessId = session.HostProcessId;
            _lostHostProcessId = null;
            return rpc;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }
}

/// <summary>A streaming call to a model host.</summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public sealed class ModelHostStream : IAsyncDisposable
{
    private readonly RpcOutgoingCall _call;

    internal ModelHostStream(RpcOutgoingCall call) => _call = call;

    /// <summary>Reads events as they arrive. Consuming events grants the host more credits.</summary>
    public async IAsyncEnumerable<JsonElement> ReadEventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var enumerator = _call.ReadEventsAsync(ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                JsonElement current;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch (Exception ex)
                {
                    throw LocalAiException.FromRpc(ex, fromHost: true);
                }

                yield return current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The final result, available after all events.</summary>
    public async Task<JsonElement?> GetResultAsync(CancellationToken ct = default)
    {
        try
        {
            return await _call.GetResultAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw LocalAiException.FromRpc(ex, fromHost: true);
        }
    }

    public ValueTask DisposeAsync() => _call.DisposeAsync();
}
