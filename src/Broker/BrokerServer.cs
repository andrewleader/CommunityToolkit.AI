using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using CommunityToolkit.AI.Protocol;
using CommunityToolkit.AI.Protocol.Runtime;
using CommunityToolkit.AI.Protocol.Trust;

namespace CommunityToolkit.AI.Broker;

/// <summary>Serves the per-user broker pipe: handshake, status, acquisition and sessions.</summary>
internal sealed class BrokerServer
{
    /// <summary>EPs this runtime build can run. Windows ML EPs (DirectML, QNN, OpenVINO, ...) are added in sprint 9.</summary>
    private static readonly string[] AvailableEps = ["cpu"];
    private static readonly Dictionary<string, string[]> DeviceEps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CPU"] = ["cpu"],
        ["GPU"] = ["dml", "gpu", "cuda"],
        ["NPU"] = ["qnn", "npu", "vitisai", "openvino"],
    };

    private readonly BrokerContext _ctx;
    private readonly CatalogService _catalog;
    private readonly ModelStore _store;
    private readonly HostManager _hosts;
    private readonly LicenseStore _licenses;
    private readonly ConcurrentDictionary<RpcConnection, ClientSession> _clients = new();
    private int _inFlight;

    public BrokerServer(BrokerContext ctx, CatalogService catalog, ModelStore store, HostManager hosts)
    {
        _ctx = ctx;
        _catalog = catalog;
        _store = store;
        _hosts = hosts;
        _licenses = new LicenseStore(ctx.Paths);
    }

    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.UtcNow;

    public int ClientCount => _clients.Count;

    public bool HasInFlightCalls => Volatile.Read(ref _inFlight) > 0;

    /// <summary>Raised when a client needs a contract this runtime does not have yet, or shutdown is requested.</summary>
    public event Action? UpdateNeeded;

    public event Action? ShutdownRequested;

    public Func<RpcIncomingCall, Task<JsonElement?>> OnConnect(RpcConnection connection)
    {
        _clients[connection] = new ClientSession();
        LastActivity = DateTimeOffset.UtcNow;
        return call => HandleAsync(connection, call);
    }

    public void OnDisconnect(RpcConnection connection)
    {
        if (_clients.TryRemove(connection, out var session))
        {
            Log.Info($"Client {session.Name} ({session.ProcessId}) disconnected.");
        }

        LastActivity = DateTimeOffset.UtcNow;
    }

    /// <summary>Closes client connections that have no call in flight. Clients reconnect transparently on their next call.</summary>
    public async Task CloseIdleClientsAsync()
    {
        foreach (var (connection, session) in _clients)
        {
            if (Volatile.Read(ref session.InFlight) == 0)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<JsonElement?> HandleAsync(RpcConnection connection, RpcIncomingCall call)
    {
        var session = _clients.GetValueOrDefault(connection) ?? new ClientSession();
        Interlocked.Increment(ref _inFlight);
        Interlocked.Increment(ref session.InFlight);
        LastActivity = DateTimeOffset.UtcNow;
        try
        {
            if (call.Method != CoreMethods.Hello && session.Hello is null)
            {
                throw new RpcException(ErrorCodes.InvalidRequest, "core.hello must be the first call.");
            }

            return call.Method switch
            {
                CoreMethods.Hello => Serialize(Hello(session, Deserialize(call, CoreJsonContext.Default.HelloRequest)), CoreJsonContext.Default.HelloResponse),
                CoreMethods.ModelStatus => Serialize(await GetStatusAsync(Deserialize(call, CoreJsonContext.Default.ModelRequest), call.CancellationToken).ConfigureAwait(false), CoreJsonContext.Default.ModelStatusResponse),
                CoreMethods.Ensure => Serialize(await EnsureAsync(call, Deserialize(call, CoreJsonContext.Default.EnsureRequest)).ConfigureAwait(false), CoreJsonContext.Default.EnsureResponse),
                CoreMethods.OpenSession => Serialize(await OpenSessionAsync(Deserialize(call, CoreJsonContext.Default.OpenSessionRequest), call.CancellationToken).ConfigureAwait(false), CoreJsonContext.Default.OpenSessionResponse),
                CoreMethods.Shutdown => Shutdown(),
                _ => throw new RpcException(ErrorCodes.NotSupported, $"Unknown method '{call.Method}'."),
            };
        }
        finally
        {
            Interlocked.Decrement(ref session.InFlight);
            Interlocked.Decrement(ref _inFlight);
            LastActivity = DateTimeOffset.UtcNow;
        }
    }

    private HelloResponse Hello(ClientSession session, HelloRequest request)
    {
        if (ContractVersions.Major(request.CoreMaxVersion) != ProtocolInfo.CoreMajor)
        {
            throw new RpcException(ErrorCodes.ProtocolTooOld, $"Core protocol {request.CoreMaxVersion} is not served on this pipe.");
        }

        session.Hello = request;
        session.Name = request.ClientName ?? "unknown";
        session.ProcessId = request.ProcessId;
        Log.Info($"Client {session.Name} ({session.ProcessId}) connected.");

        var response = new HelloResponse { RuntimeVersion = _ctx.RuntimeVersion, Capabilities = ["ensure.progress", "sessions", "catalog.hf"] };
        foreach (var (task, maxVersion) in request.Tasks)
        {
            if (SupportedContracts.Negotiate(task, maxVersion, out _) is { } negotiated)
            {
                response.Tasks[task] = negotiated;
                string marker = $"{task}/{ContractVersions.Major(negotiated)}";
                if (_catalog.Manifest?.DeprecatedContracts.Contains(marker) == true)
                {
                    response.Deprecated.Add(marker);
                }
            }
        }

        return response;
    }

    private async Task<ModelStatusResponse> GetStatusAsync(ModelRequest request, CancellationToken ct)
    {
        var (catalogModel, installed) = await ResolveAsync(request.ModelId, request.Task, ct).ConfigureAwait(false);
        var response = new ModelStatusResponse { ModelVersion = catalogModel?.Version ?? installed?.Version, DisplayName = catalogModel?.DisplayName };
        if (catalogModel is not null)
        {
            response.License = new LicenseDto
            {
                Id = catalogModel.License.Id,
                Url = catalogModel.License.Url,
                RequiresAcceptance = catalogModel.License.RequiresAcceptance,
                Accepted = !catalogModel.License.RequiresAcceptance || _licenses.IsAccepted(catalogModel.Id, catalogModel.License.Id),
            };
        }

        if (installed is not null)
        {
            response.Status = "Ready";
            return response;
        }

        if (catalogModel is null)
        {
            throw new RpcException(ErrorCodes.Offline, $"'{request.ModelId}' is not installed and the catalog cannot be reached.");
        }

        if (UnsupportedReason(catalogModel, request.Device) is { } reason)
        {
            response.Status = "Unsupported";
            response.Reason = reason;
            return response;
        }

        response.Status = _store.IsDownloading(catalogModel.Id) ? "Downloading" : "NeedsModel";
        response.DownloadBytes = _store.RemainingBytes(catalogModel);
        return response;
    }

    private async Task<EnsureResponse> EnsureAsync(RpcIncomingCall call, EnsureRequest request)
    {
        var ct = call.CancellationToken;
        var (catalogModel, installed) = await ResolveAsync(request.ModelId, request.Task, ct).ConfigureAwait(false);
        if (installed is not null)
        {
            return new EnsureResponse { ModelVersion = installed.Version };
        }

        if (catalogModel is null)
        {
            throw new RpcException(ErrorCodes.Offline, $"'{request.ModelId}' is not installed and the catalog cannot be reached.");
        }

        if (UnsupportedReason(catalogModel, request.Device) is { } reason)
        {
            throw new RpcException(ErrorCodes.DeviceUnsupported, reason);
        }

        if (catalogModel.License.RequiresAcceptance && !_licenses.IsAccepted(catalogModel.Id, catalogModel.License.Id))
        {
            if (request.AcceptedLicenseId != catalogModel.License.Id)
            {
                throw new RpcException(ErrorCodes.LicenseNotAccepted, $"The licence '{catalogModel.License.Id}' for '{catalogModel.Id}' has not been accepted.");
            }

            _licenses.Accept(catalogModel.Id, catalogModel.License.Id);
        }

        if (request.Policy == "Never")
        {
            throw new RpcException(ErrorCodes.NotDownloaded, $"'{catalogModel.Id}' is not downloaded and the acquisition policy is Never.");
        }

        // Progress is produced on download threads; a latest-value channel decouples it from the credit-limited pipe.
        var events = Channel.CreateBounded<ProgressEvent>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        var pump = Task.Run(async () =>
        {
            await foreach (var evt in events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await call.SendEventAsync(JsonSerializer.SerializeToElement(evt, CoreJsonContext.Default.ProgressEvent)).ConfigureAwait(false);
            }
        }, ct);

        long lastTick = 0;
        try
        {
            var result = await _store.InstallAsync(catalogModel, (done, total) =>
            {
                long now = Environment.TickCount64;
                if (now - lastTick < 100 && done < total)
                {
                    return;
                }

                lastTick = now;
                events.Writer.TryWrite(new ProgressEvent { Stage = "Model", BytesDone = done, BytesTotal = total, Fraction = total > 0 ? (double)done / total : 0 });
            }, ct).ConfigureAwait(false);

            events.Writer.TryWrite(new ProgressEvent { Stage = "Verify", BytesDone = result.Files.Sum(f => f.Size), BytesTotal = result.Files.Sum(f => f.Size), Fraction = 1 });
            return new EnsureResponse { ModelVersion = result.Version };
        }
        finally
        {
            events.Writer.TryComplete();
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or RpcConnectionLostException)
            {
            }
        }
    }

    private async Task<OpenSessionResponse> OpenSessionAsync(OpenSessionRequest request, CancellationToken ct)
    {
        if (SupportedContracts.Negotiate(request.Task, request.ContractVersion, out string? error) is null)
        {
            if (error == ErrorCodes.ProtocolTooOld)
            {
                UpdateNeeded?.Invoke();
            }

            throw new RpcException(error ?? ErrorCodes.NotSupported, $"This runtime ({_ctx.RuntimeVersion}) does not serve the {request.Task} contract {request.ContractVersion}.");
        }

        var (catalogModel, installed) = await ResolveAsync(request.ModelId, request.Task, ct).ConfigureAwait(false);
        if (installed is null)
        {
            throw new RpcException(ErrorCodes.NotDownloaded, $"'{request.ModelId}' is not downloaded. Call EnsureReadyAsync first.");
        }

        string ep = SelectEp(catalogModel?.Hardware.Eps ?? ["cpu"], request.Device)
            ?? throw new RpcException(ErrorCodes.DeviceUnsupported, $"No available execution provider can run '{request.ModelId}'.");
        var host = await _hosts.GetOrStartAsync(installed, ep, request.FailedHostProcessId, ct).ConfigureAwait(false);
        _store.TouchLastUsed(installed);
        return new OpenSessionResponse { HostPipe = host.Pipe, ModelVersion = installed.Version, Ep = ep, HostProcessId = host.Process.Id };
    }

    private JsonElement? Shutdown()
    {
        Log.Info("Shutdown requested by a client.");
        _ = Task.Delay(100).ContinueWith(_ => ShutdownRequested?.Invoke(), TaskScheduler.Default);
        return null;
    }

    private async Task<(CatalogModel? Catalog, InstalledModel? Installed)> ResolveAsync(string id, string task, CancellationToken ct)
    {
        if (!LocalAIPaths.IsSafeName(id))
        {
            throw new RpcException(ErrorCodes.ModelUnknown, $"'{id}' is not a valid model id.");
        }

        var catalogModel = await _catalog.FindAsync(id, ct).ConfigureAwait(false);
        if (catalogModel is not null)
        {
            if (catalogModel.Task != task)
            {
                throw new RpcException(ErrorCodes.ModelUnknown, $"'{id}' is a {catalogModel.Task} model, not a {task} model.");
            }

            if (catalogModel.MinRuntimeVersion is { } min && SemVer.Compare(_ctx.RuntimeVersion, min) < 0)
            {
                UpdateNeeded?.Invoke();
                throw new RpcException(ErrorCodes.ProtocolTooOld, $"'{id}' needs runtime {min} or later; this runtime is {_ctx.RuntimeVersion}. An update has been requested.");
            }

            return (catalogModel, _store.GetInstalled(catalogModel.Id, catalogModel.Version));
        }

        var installed = _store.FindNewestInstalled(id);
        if (installed is null && _catalog.Manifest is null && await _catalog.GetCatalogAsync(false, ct).ConfigureAwait(false) is null)
        {
            throw new RpcException(ErrorCodes.Offline, "The model catalog cannot be reached.");
        }

        if (installed is null || installed.Task != task)
        {
            throw new RpcException(ErrorCodes.ModelUnknown, $"'{id}' is not a {task} model in the catalog.");
        }

        return (null, installed);
    }

    private static string? UnsupportedReason(CatalogModel model, string device)
    {
        var (total, _) = MemoryInfo.Get();
        if (model.Hardware.MinMemoryMB > 0 && total > 0 && total / (1024 * 1024) < (ulong)model.Hardware.MinMemoryMB)
        {
            return $"'{model.Id}' needs {model.Hardware.MinMemoryMB} MB of memory.";
        }

        return SelectEp(model.Hardware.Eps, device) is null ? $"'{model.Id}' needs one of [{string.Join(", ", model.Hardware.Eps)}], which this device cannot provide." : null;
    }

    /// <summary>Picks an EP: the device preference first, then the catalog's order. Device is a preference only.</summary>
    private static string? SelectEp(IReadOnlyList<string> preferred, string device)
    {
        var candidates = preferred.Where(ep => AvailableEps.Contains(ep, StringComparer.OrdinalIgnoreCase)).ToList();
        if (DeviceEps.TryGetValue(device, out var forDevice) && candidates.FirstOrDefault(ep => forDevice.Contains(ep, StringComparer.OrdinalIgnoreCase)) is { } match)
        {
            return match;
        }

        return candidates.FirstOrDefault();
    }

    private static T Deserialize<T>(RpcIncomingCall call, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        call.Payload is { } payload ? payload.Deserialize(info) ?? throw new RpcException(ErrorCodes.InvalidRequest, "Empty payload.") : throw new RpcException(ErrorCodes.InvalidRequest, "Missing payload.");

    private static JsonElement Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) => JsonSerializer.SerializeToElement(value, info);

    private sealed class ClientSession
    {
        public int InFlight;

        public HelloRequest? Hello { get; set; }

        public string Name { get; set; } = "unknown";

        public int ProcessId { get; set; }
    }
}

/// <summary>Accepted model licences, per user.</summary>
internal sealed class LicenseStore
{
    private readonly LocalAIPaths _paths;
    private readonly object _lock = new();

    public LicenseStore(LocalAIPaths paths) => _paths = paths;

    public bool IsAccepted(string modelId, string licenseId)
    {
        lock (_lock)
        {
            return Load().Accepted.TryGetValue(modelId, out var accepted) && accepted == licenseId;
        }
    }

    public void Accept(string modelId, string licenseId)
    {
        lock (_lock)
        {
            var state = Load();
            state.Accepted[modelId] = licenseId;
            AtomicFile.WriteAllBytes(_paths.LicensesFile, JsonSerializer.SerializeToUtf8Bytes(state, RuntimeJsonContext.Default.LicenseState));
        }
    }

    private LicenseState Load()
    {
        try
        {
            if (File.Exists(_paths.LicensesFile))
            {
                return JsonSerializer.Deserialize(File.ReadAllBytes(_paths.LicensesFile), RuntimeJsonContext.Default.LicenseState) ?? new LicenseState();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }

        return new LicenseState();
    }
}
