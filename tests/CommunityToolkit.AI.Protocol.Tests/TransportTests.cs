using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.AI.Protocol;
using CommunityToolkit.AI.Protocol.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CommunityToolkit.AI.Protocol.Tests;

public class TransportTests
{
    private static async Task<(RpcConnection Client, RpcConnection Server)> ConnectAsync(Func<RpcIncomingCall, Task<JsonElement?>> handler)
    {
        string name = "localai-test-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());
        var serverRpc = new RpcConnection(server, handler);
        var clientRpc = new RpcConnection(client);
        serverRpc.Start();
        clientRpc.Start();
        return (clientRpc, serverRpc);
    }

    [Fact]
    public async Task Streaming_RespectsCreditBackpressure()
    {
        int sent = 0;
        var (client, server) = await ConnectAsync(async call =>
        {
            for (int i = 0; i < 50; i++)
            {
                await call.SendEventAsync(JsonDocument.Parse(i.ToString()).RootElement);
                Interlocked.Increment(ref sent);
            }

            return JsonDocument.Parse("\"done\"").RootElement;
        });

        await using var call = await client.StartCallAsync("test.stream", null, initialCredits: 4, CancellationToken.None);
        await Task.Delay(300);
        Assert.Equal(4, Volatile.Read(ref sent));

        var received = new List<int>();
        await foreach (var evt in call.ReadEventsAsync())
        {
            received.Add(evt.GetInt32());
        }

        Assert.Equal(Enumerable.Range(0, 50), received);
        Assert.Equal("done", (await call.GetResultAsync())?.GetString());
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Cancellation_ReachesTheCallee()
    {
        var calleeCancelled = new TaskCompletionSource();
        var (client, server) = await ConnectAsync(async call =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, call.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                calleeCancelled.SetResult();
                throw;
            }

            return null;
        });

        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.InvokeAsync("test.wait", null, cts.Token));
        await calleeCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Errors_AndDisconnects_AreTyped()
    {
        var (client, server) = await ConnectAsync(call => call.Method == "test.fail"
            ? throw new RpcException(ErrorCodes.LicenseNotAccepted, "nope")
            : Task.Delay(Timeout.Infinite, call.CancellationToken).ContinueWith(_ => (JsonElement?)null));

        var error = await Assert.ThrowsAsync<RpcException>(() => client.InvokeAsync("test.fail", null));
        Assert.Equal(ErrorCodes.LicenseNotAccepted, error.Code);

        var pending = client.InvokeAsync("test.hang", null);
        await Task.Delay(100);
        await server.DisposeAsync();
        await Assert.ThrowsAsync<RpcConnectionLostException>(() => pending);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Downloader_ResumesVerifiesAndEnforcesAllowList()
    {
        byte[] data = RandomNumberGenerator.GetBytes(300_000);
        string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        int flakyCalls = 0;
        long? resumedFrom = null;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/file", () => Results.Bytes(data, "application/octet-stream", enableRangeProcessing: true));
        app.MapGet("/redirect", () => Results.Redirect("/file"));
        app.MapGet("/offsite", (HttpContext c) => Results.Redirect($"http://localhost:{c.Connection.LocalPort}/file"));
        app.MapGet("/flaky", async (HttpContext context) =>
        {
            if (Interlocked.Increment(ref flakyCalls) == 1)
            {
                context.Response.ContentLength = data.Length;
                await context.Response.Body.WriteAsync(data.AsMemory(0, 100_000));
                await context.Response.Body.FlushAsync();
                context.Abort();
                return;
            }

            resumedFrom = context.Request.GetTypedHeaders().Range?.Ranges.First().From;
            await Results.Bytes(data, "application/octet-stream", enableRangeProcessing: true).ExecuteAsync(context);
        });
        await app.StartAsync();
        string root = app.Urls.First();

        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var http = SecureDownloader.CreateHttpClient("tests");
        var downloader = new SecureDownloader(http, uri => SecureDownloader.HostMatches(uri.Host, ["127.0.0.1"]));

        string viaRedirect = Path.Combine(dir, "a.bin");
        await downloader.DownloadAsync(new Uri(root + "/redirect"), viaRedirect, data.Length, sha, viaRedirect + ".partial", null, CancellationToken.None);
        Assert.Equal(data, await File.ReadAllBytesAsync(viaRedirect));

        string resumed = Path.Combine(dir, "b.bin");
        await downloader.DownloadAsync(new Uri(root + "/flaky"), resumed, data.Length, sha, resumed + ".partial", null, CancellationToken.None);
        Assert.Equal(data, await File.ReadAllBytesAsync(resumed));
        Assert.True(resumedFrom > 0, "The second request should resume with a Range header.");

        string bad = Path.Combine(dir, "c.bin");
        var integrity = await Assert.ThrowsAsync<RpcException>(() => downloader.DownloadAsync(new Uri(root + "/file"), bad, data.Length, new string('0', 64), bad + ".partial", null, CancellationToken.None));
        Assert.Equal(ErrorCodes.IntegrityFailure, integrity.Code);
        Assert.False(File.Exists(bad));
        Assert.False(File.Exists(bad + ".partial"));

        string offsite = Path.Combine(dir, "d.bin");
        var blocked = await Assert.ThrowsAsync<RpcException>(() => downloader.DownloadAsync(new Uri(root + "/offsite"), offsite, data.Length, sha, offsite + ".partial", null, CancellationToken.None));
        Assert.Equal(ErrorCodes.IntegrityFailure, blocked.Code);
    }
}
