using System.Text.Json;
using System.Threading.Channels;
using CommunityToolkit.AI.Host.Adapters;
using CommunityToolkit.AI.Protocol;
using CommunityToolkit.AI.Protocol.Chat;
using CommunityToolkit.AI.Protocol.Runtime;

namespace CommunityToolkit.AI.Host;

/// <summary>Serves one model to any number of client sessions over the host pipe.</summary>
internal sealed class HostServer
{
    private readonly ModelInfo _model;
    private readonly ITaskAdapter _adapter;
    private readonly FairScheduler _scheduler = new();
    private int _inFlight;
    private int _connections;

    public HostServer(ModelInfo model, ITaskAdapter adapter)
    {
        _model = model;
        _adapter = adapter;
    }

    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.UtcNow;

    public bool Busy => Volatile.Read(ref _inFlight) > 0;

    public int Connections => Volatile.Read(ref _connections);

    public Func<RpcIncomingCall, Task<JsonElement?>> OnConnect(RpcConnection connection)
    {
        Interlocked.Increment(ref _connections);
        var session = new HostSession();
        connection.State = session;
        return call => HandleAsync(session, call);
    }

    public void OnDisconnect(RpcConnection connection)
    {
        Interlocked.Decrement(ref _connections);
        (connection.State as HostSession)?.AdapterSession?.Dispose();
    }

    private async Task<JsonElement?> HandleAsync(HostSession session, RpcIncomingCall call)
    {
        Interlocked.Increment(ref _inFlight);
        LastActivity = DateTimeOffset.UtcNow;
        try
        {
            if (call.Method == CoreMethods.HostHello)
            {
                return Hello(session, call);
            }

            if (session.ContractVersion is null)
            {
                throw new RpcException(ErrorCodes.InvalidRequest, "host.hello must be the first call.");
            }

            return (_adapter, call.Method) switch
            {
                (IChatAdapter chat, ChatContract.Complete) => await ChatCompleteAsync(chat, session, call).ConfigureAwait(false),
                (IChatAdapter, ChatContract.Reset) => ResetSession(session),
                _ => throw new RpcException(ErrorCodes.NotSupported, $"'{call.Method}' is not supported by the {_adapter.Task} host for contract {session.ContractVersion}."),
            };
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            LastActivity = DateTimeOffset.UtcNow;
        }
    }

    private JsonElement? Hello(HostSession session, RpcIncomingCall call)
    {
        var request = call.Payload?.Deserialize(CoreJsonContext.Default.HostHelloRequest) ?? throw new RpcException(ErrorCodes.InvalidRequest, "Missing payload.");
        if (request.Task != _adapter.Task)
        {
            throw new RpcException(ErrorCodes.ModelUnknown, $"This host serves {_adapter.Task}, not {request.Task}.");
        }

        string negotiated = SupportedContracts.Negotiate(request.Task, request.ContractVersion, out string? error)
            ?? throw new RpcException(error ?? ErrorCodes.NotSupported, $"Contract {request.Task} {request.ContractVersion} is not served.");
        session.ContractVersion = negotiated;
        session.ClientProcessId = request.ClientProcessId;
        session.AdapterSession = _adapter.CreateSession();
        return JsonSerializer.SerializeToElement(
            new HostHelloResponse { ModelId = _model.Id, ModelVersion = _model.Version, Ep = _model.Ep, ContractVersion = negotiated },
            CoreJsonContext.Default.HostHelloResponse);
    }

    private JsonElement? ResetSession(HostSession session)
    {
        session.AdapterSession?.Dispose();
        session.AdapterSession = _adapter.CreateSession();
        return null;
    }

    private async Task<JsonElement?> ChatCompleteAsync(IChatAdapter chat, HostSession session, RpcIncomingCall call)
    {
        var request = call.Payload?.Deserialize(ChatJsonContext.Default.ChatCompletionRequest) ?? throw new RpcException(ErrorCodes.InvalidRequest, "Missing payload.");
        ValidateChat(request);
        var ct = call.CancellationToken;
        using var turn = await _scheduler.EnterAsync(session.ClientProcessId, ct).ConfigureAwait(false);

        // A small bounded channel makes a slow reader (out of credits) pause generation instead of buffering.
        var pieces = Channel.CreateBounded<string>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        var generation = Task.Factory.StartNew(() =>
        {
            try
            {
                return chat.Generate(session.AdapterSession, request, piece => pieces.Writer.WriteAsync(piece, ct).AsTask().GetAwaiter().GetResult(), ct);
            }
            finally
            {
                pieces.Writer.TryComplete();
            }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        await foreach (var piece in pieces.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await call.SendEventAsync(JsonSerializer.SerializeToElement(new ChatChunk { Text = piece }, ChatJsonContext.Default.ChatChunk)).ConfigureAwait(false);
        }

        var result = await generation.ConfigureAwait(false);
        result.ModelId = _model.Id;
        return JsonSerializer.SerializeToElement(result, ChatJsonContext.Default.ChatCompletionResult);
    }

    private static void ValidateChat(ChatCompletionRequest request)
    {
        const int MaxMessages = 4096;
        const int MaxChars = 4 * 1024 * 1024;
        if (request.Messages.Count == 0 || request.Messages.Count > MaxMessages)
        {
            throw new RpcException(ErrorCodes.InvalidRequest, $"A request needs between 1 and {MaxMessages} messages.");
        }

        if (request.Messages.Sum(m => (long)m.Text.Length) > MaxChars)
        {
            throw new RpcException(ErrorCodes.InvalidRequest, "The conversation is too large.");
        }

        foreach (var message in request.Messages)
        {
            if (message.Role is not ("system" or "user" or "assistant" or "tool"))
            {
                throw new RpcException(ErrorCodes.InvalidRequest, $"Unknown role '{message.Role}'.");
            }
        }

        if (request.Options is { } o && (o.MaxOutputTokens is < 1 || o.Temperature is < 0 or > 10 || o.TopP is < 0 or > 1 || o.TopK is < 0 || o.StopSequences?.Count > 16))
        {
            throw new RpcException(ErrorCodes.InvalidRequest, "Invalid chat options.");
        }
    }

    private sealed class HostSession
    {
        public string? ContractVersion { get; set; }

        public int ClientProcessId { get; set; }

        public IDisposable? AdapterSession { get; set; }
    }
}
