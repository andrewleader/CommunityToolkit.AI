using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace CommunityToolkit.AI.Protocol.Runtime;

/// <summary>
/// Accept loop for a named pipe whose ACL allows only the current user. Each connection gets its own <see cref="RpcConnection"/>.
/// </summary>
internal sealed class PipeServer
{
    private readonly string _name;
    private readonly Func<RpcConnection, Func<RpcIncomingCall, Task<JsonElement?>>> _onConnect;
    private readonly Action<RpcConnection>? _onDisconnect;

    public PipeServer(string name, Func<RpcConnection, Func<RpcIncomingCall, Task<JsonElement?>>> onConnect, Action<RpcConnection>? onDisconnect = null)
    {
        _name = name;
        _onConnect = onConnect;
        _onDisconnect = onDisconnect;
    }

    public static PipeSecurity CurrentUserOnly()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No user SID.");
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>Creates the first instance. Throws if another process already owns the name (anti-squatting).</summary>
    public NamedPipeServerStream CreateFirstInstance() => Create(first: true);

    private NamedPipeServerStream Create(bool first) =>
        NamedPipeServerStreamAcl.Create(
            _name,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : 0),
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            CurrentUserOnly());

    public async Task RunAsync(NamedPipeServerStream? firstInstance, CancellationToken ct)
    {
        var next = firstInstance ?? Create(first: true);
        while (!ct.IsCancellationRequested)
        {
            var current = next;
            try
            {
                await current.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await current.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (IOException)
            {
                await current.DisposeAsync().ConfigureAwait(false);
                next = Create(first: false);
                continue;
            }

            next = Create(first: false);
            _ = Task.Run(() => ServeAsync(current));
        }
    }

    private async Task ServeAsync(NamedPipeServerStream stream)
    {
        Func<RpcIncomingCall, Task<JsonElement?>>? handler = null;
        var connection = new RpcConnection(stream, call => handler!(call));
        try
        {
            handler = _onConnect(connection);
            connection.Start();
            await connection.Completion.ConfigureAwait(false);
        }
        finally
        {
            _onDisconnect?.Invoke(connection);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
