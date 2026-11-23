using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace CommunityToolkit.AI.Protocol;

/// <summary>The connection to the peer was lost while a call was in flight.</summary>
internal sealed class RpcConnectionLostException : IOException
{
    public RpcConnectionLostException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Bidirectional request/response/event RPC over a stream of frames. Used by the client, the broker and the hosts.
/// Streaming responses use credit-based backpressure: the caller grants credits and the callee
/// may only send as many events as it has credits.
/// </summary>
internal sealed class RpcConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly Func<RpcIncomingCall, Task<JsonElement?>>? _handler;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, RpcOutgoingCall> _pending = new();
    private readonly ConcurrentDictionary<long, RpcIncomingCall> _incoming = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private int _started;
    private bool _closed;

    public RpcConnection(Stream stream, Func<RpcIncomingCall, Task<JsonElement?>>? handler = null)
    {
        _stream = stream;
        _handler = handler;
    }

    /// <summary>Completes when the read loop ends. The result is the fault, or null on a clean close.</summary>
    public Task<Exception?> Completion => _completion.Task;

    public bool IsConnected => !Volatile.Read(ref _closed) && !_completion.Task.IsCompleted;

    /// <summary>Arbitrary per-connection state owned by the server (for example a session).</summary>
    public object? State { get; set; }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _ = Task.Run(ReadLoopAsync);
        }
    }

    public async Task<JsonElement?> InvokeAsync(string method, JsonElement? payload, CancellationToken ct = default)
    {
        await using var call = await StartCallAsync(method, payload, initialCredits: null, ct).ConfigureAwait(false);
        return await call.GetResultAsync(ct).ConfigureAwait(false);
    }

    public async Task<RpcOutgoingCall> StartCallAsync(string method, JsonElement? payload, int? initialCredits, CancellationToken ct)
    {
        if (!IsConnected)
        {
            throw new RpcConnectionLostException("The connection is closed.");
        }

        long id = Interlocked.Increment(ref _nextId);
        var call = new RpcOutgoingCall(this, id, initialCredits);
        _pending[id] = call;
        try
        {
            await SendAsync(new Envelope { Id = id, Kind = EnvelopeKind.Request, Method = method, Payload = payload, Credits = initialCredits }, ct).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        return call;
    }

    internal async Task SendAsync(Envelope envelope, CancellationToken ct)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(envelope, CoreJsonContext.Default.Envelope);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Framing.WriteFrameAsync(_stream, body, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Volatile.Write(ref _closed, true);
            throw new RpcConnectionLostException("The connection was lost while sending.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal void Forget(long id) => _pending.TryRemove(id, out _);

    private async Task ReadLoopAsync()
    {
        Exception? fault = null;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                byte[]? frame = await Framing.ReadFrameAsync(_stream, _shutdown.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                Envelope? envelope = JsonSerializer.Deserialize(frame, CoreJsonContext.Default.Envelope);
                if (envelope is not null)
                {
                    Dispatch(envelope);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            fault = ex;
        }

        var lost = new RpcConnectionLostException("The connection to the peer was lost.", fault);
        Volatile.Write(ref _closed, true);
        foreach (var call in _pending.Values)
        {
            call.Fail(lost);
        }

        _pending.Clear();
        foreach (var incoming in _incoming.Values)
        {
            incoming.CancelFromPeer();
        }

        _completion.TrySetResult(fault);
    }

    private void Dispatch(Envelope envelope)
    {
        switch (envelope.Kind)
        {
            case EnvelopeKind.Request:
                var incoming = new RpcIncomingCall(this, envelope.Id, envelope.Method ?? "", envelope.Payload, envelope.Credits, _shutdown.Token);
                _incoming[envelope.Id] = incoming;
                _ = Task.Run(() => HandleIncomingAsync(incoming));
                break;
            case EnvelopeKind.Response:
                if (_pending.TryRemove(envelope.Id, out var done))
                {
                    done.Complete(envelope.Payload);
                }

                break;
            case EnvelopeKind.Error:
                if (_pending.TryRemove(envelope.Id, out var failed))
                {
                    var error = envelope.Error ?? new WireError();
                    failed.Fail(new RpcException(error.Code, error.Message, error.Data));
                }

                break;
            case EnvelopeKind.Event:
                if (_pending.TryGetValue(envelope.Id, out var streaming) && envelope.Payload is { } evt)
                {
                    streaming.PostEvent(evt);
                }

                break;
            case EnvelopeKind.Credit:
                if (_incoming.TryGetValue(envelope.Id, out var creditTarget))
                {
                    creditTarget.AddCredits(envelope.Credits ?? 0);
                }

                break;
            case EnvelopeKind.Cancel:
                if (_incoming.TryGetValue(envelope.Id, out var cancelTarget))
                {
                    cancelTarget.CancelFromPeer();
                }

                break;
        }
    }

    private async Task HandleIncomingAsync(RpcIncomingCall call)
    {
        Envelope reply;
        try
        {
            if (_handler is null)
            {
                throw new RpcException(ErrorCodes.NotSupported, "This endpoint does not accept requests.");
            }

            JsonElement? result = await _handler(call).ConfigureAwait(false);
            reply = new Envelope { Id = call.Id, Kind = EnvelopeKind.Response, Payload = result };
        }
        catch (RpcException ex)
        {
            reply = ErrorEnvelope(call.Id, ex.Code, ex.Message, ex.ErrorData);
        }
        catch (OperationCanceledException)
        {
            reply = ErrorEnvelope(call.Id, ErrorCodes.Cancelled, "The operation was cancelled.", null);
        }
        catch (Exception ex)
        {
            reply = ErrorEnvelope(call.Id, ErrorCodes.Internal, ex.Message, null);
        }
        finally
        {
            _incoming.TryRemove(call.Id, out _);
        }

        try
        {
            await SendAsync(reply, CancellationToken.None).ConfigureAwait(false);
        }
        catch (RpcConnectionLostException)
        {
        }
        finally
        {
            call.Dispose();
        }
    }

    private static Envelope ErrorEnvelope(long id, string code, string message, JsonElement? data) =>
        new() { Id = id, Kind = EnvelopeKind.Error, Error = new WireError { Code = code, Message = message, Data = data } };

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        await Task.WhenAny(_completion.Task, Task.Delay(2000)).ConfigureAwait(false);
        _completion.TrySetResult(null);
    }
}

/// <summary>A call made to the peer. Events stream through <see cref="ReadEventsAsync"/>; the final result through <see cref="GetResultAsync"/>.</summary>
internal sealed class RpcOutgoingCall : IAsyncDisposable
{
    private const int CreditBatch = 8;
    private readonly RpcConnection _connection;
    private readonly int? _initialCredits;
    private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<JsonElement?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _consumedSinceGrant;

    public RpcOutgoingCall(RpcConnection connection, long id, int? initialCredits)
    {
        _connection = connection;
        Id = id;
        _initialCredits = initialCredits;
    }

    public long Id { get; }

    internal void PostEvent(JsonElement evt) => _events.Writer.TryWrite(evt);

    internal void Complete(JsonElement? payload)
    {
        _events.Writer.TryComplete();
        _result.TrySetResult(payload);
    }

    internal void Fail(Exception ex)
    {
        _events.Writer.TryComplete();
        _result.TrySetException(ex);
    }

    public async IAsyncEnumerable<JsonElement> ReadEventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var registration = ct.Register(() => _ = CancelAsync());
        while (await _events.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (_events.Reader.TryRead(out var evt))
            {
                yield return evt;
                await GrantAsync().ConfigureAwait(false);
            }
        }

        // Surface the error, if any, once all events have been consumed.
        await _result.Task.ConfigureAwait(false);
    }

    public async Task<JsonElement?> GetResultAsync(CancellationToken ct = default)
    {
        using var registration = ct.Register(() => _ = CancelAsync());
        try
        {
            return await _result.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _connection.Forget(Id);
            throw;
        }
    }

    private async Task GrantAsync()
    {
        if (_initialCredits is null || _result.Task.IsCompleted)
        {
            return;
        }

        if (++_consumedSinceGrant >= Math.Min(CreditBatch, Math.Max(1, _initialCredits.Value / 2)))
        {
            int grant = _consumedSinceGrant;
            _consumedSinceGrant = 0;
            try
            {
                await _connection.SendAsync(new Envelope { Id = Id, Kind = EnvelopeKind.Credit, Credits = grant }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (RpcConnectionLostException)
            {
            }
        }
    }

    private async Task CancelAsync()
    {
        if (_result.Task.IsCompleted)
        {
            return;
        }

        try
        {
            await _connection.SendAsync(new Envelope { Id = Id, Kind = EnvelopeKind.Cancel }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (RpcConnectionLostException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_result.Task.IsCompleted)
        {
            await CancelAsync().ConfigureAwait(false);
            _connection.Forget(Id);
            Fail(new OperationCanceledException());
        }

        _ = _result.Task.Exception;
    }
}

/// <summary>A call received from the peer.</summary>
internal sealed class RpcIncomingCall : IDisposable
{
    private readonly RpcConnection _connection;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim? _credits;

    public RpcIncomingCall(RpcConnection connection, long id, string method, JsonElement? payload, int? initialCredits, CancellationToken shutdown)
    {
        _connection = connection;
        Id = id;
        Method = method;
        Payload = payload;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        _credits = initialCredits is int c ? new SemaphoreSlim(Math.Max(0, c), int.MaxValue) : null;
    }

    public long Id { get; }

    public string Method { get; }

    public JsonElement? Payload { get; }

    public RpcConnection Connection => _connection;

    /// <summary>Cancelled when the caller cancels or disconnects.</summary>
    public CancellationToken CancellationToken => _cts.Token;

    public async Task SendEventAsync(JsonElement evt)
    {
        if (_credits is not null)
        {
            await _credits.WaitAsync(_cts.Token).ConfigureAwait(false);
        }

        await _connection.SendAsync(new Envelope { Id = Id, Kind = EnvelopeKind.Event, Payload = evt }, _cts.Token).ConfigureAwait(false);
    }

    internal void AddCredits(int credits)
    {
        if (credits > 0)
        {
            _credits?.Release(credits);
        }
    }

    internal void CancelFromPeer()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
        _credits?.Dispose();
    }
}
