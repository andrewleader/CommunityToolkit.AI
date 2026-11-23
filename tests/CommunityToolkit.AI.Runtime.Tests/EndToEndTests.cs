using CommunityToolkit.AI.Chat;
using CommunityToolkit.AI.Ocr;
using Microsoft.Extensions.AI;

namespace CommunityToolkit.AI.Runtime.Tests;

/// <summary>End-to-end: NuGet-facing API -> bootstrapper -> broker -> host, against a locally served, signed site.</summary>
public class EndToEndTests(SiteFixture site) : IClassFixture<SiteFixture>
{
    private static readonly ModelOptions AcceptAll = new()
    {
        AcquisitionPolicy = AcquisitionPolicy.Silent,
        LicenseAcceptance = (_, _) => ValueTask.FromResult(true),
    };

    [Fact]
    public async Task FirstRun_InstallsRuntimeAndModel_ThenChats()
    {
        string home = site.UseFreshHome();
        var model = ChatModel.FromCatalog("test-echo-chat");
        Assert.Equal(ModelStatus.NeedsRuntime, await model.GetStatusAsync());

        // Without a licence callback the download is refused, but the runtime is installed and started.
        await Assert.ThrowsAsync<LicenseNotAcceptedException>(() => model.EnsureReadyAsync());
        Assert.Equal(ModelStatus.NeedsModel, await model.GetStatusAsync());

        var reports = new List<EnsureProgress>();
        var accepted = ChatModel.FromCatalog("test-echo-chat", AcceptAll);
        await accepted.EnsureReadyAsync(new SyncProgress<EnsureProgress>(reports.Add));
        Assert.Contains(reports, r => r.Stage == EnsureStage.Model && r.BytesTotal == 200_000);
        Assert.Equal(1.0, reports[^1].OverallFraction);
        Assert.Equal(ModelStatus.Ready, await model.GetStatusAsync());
        Assert.True(File.Exists(Path.Combine(home, "models", "test-echo-chat", "1.0.0", "weights.bin")));

        // The licence is remembered, so the plain handle is ready too.
        await model.EnsureReadyAsync();

        IChatClient client = await model.GetClientAsync();
        var response = await client.GetResponseAsync("hello local world");
        Assert.Equal("Echo: hello local world", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal("test-echo-chat", client.GetService<ChatClientMetadata>()?.DefaultModelId);

        var updates = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync("one two three"))
        {
            if (update.Text.Length > 0)
            {
                updates.Add(update.Text);
            }
        }

        Assert.Equal(["Echo:", " one", " two", " three"], updates);

        var limited = await client.GetResponseAsync("a b c d", new ChatOptions { MaxOutputTokens = 2 });
        Assert.Equal("Echo: a", limited.Text);
        Assert.Equal(ChatFinishReason.Length, limited.FinishReason);

        // A second session for the same model shares the one host process.
        IChatClient second = await ChatModel.FromCatalog("test-echo-chat").GetClientAsync();
        Assert.Equal("Echo: again", (await second.GetResponseAsync("again")).Text);
        Assert.Single(SiteFixture.ProcessesUnder(home, "host"));
        Assert.Single(SiteFixture.ProcessesUnder(home, "broker"));
    }

    [Fact]
    public async Task HostCrash_FailsInFlightCall_ThenRestartsLazily()
    {
        string home = site.UseFreshHome();
        var model = ChatModel.FromCatalog("test-echo-chat", AcceptAll);
        await model.EnsureReadyAsync();
        IChatClient client = await model.GetClientAsync();
        int firstHost = Assert.Single(SiteFixture.ProcessesUnder(home, "host")).Id;

        await Assert.ThrowsAsync<HostCrashedException>(() => client.GetResponseAsync("__crash__"));

        Assert.Equal("Echo: back", (await client.GetResponseAsync("back")).Text);
        var hosts = SiteFixture.ProcessesUnder(home, "host").Where(p => p.Id != firstHost).ToList();
        Assert.Single(hosts);
    }

    [Fact]
    public async Task Cancellation_StopsGeneration_AndTheSessionStaysUsable()
    {
        site.UseFreshHome();
        var model = ChatModel.FromCatalog("test-slow-chat", AcceptAll);
        await model.EnsureReadyAsync();
        IChatClient client = await model.GetClientAsync();

        using var cts = new CancellationTokenSource();
        int received = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync("a b c d e f g h i j k l", cancellationToken: cts.Token))
            {
                if (++received == 2)
                {
                    cts.Cancel();
                }
            }
        });

        Assert.True(received < 10);
        Assert.Equal("Echo: ok", (await client.GetResponseAsync("ok")).Text);
    }

    [Fact]
    public async Task Errors_AreTyped()
    {
        site.UseFreshHome();

        // A chat id used through another task's package is unknown for that task.
        await Assert.ThrowsAsync<ModelUnknownException>(() => OcrModel.FromCatalog("test-echo-chat", AcceptAll).EnsureReadyAsync());
        await Assert.ThrowsAsync<ModelUnknownException>(() => ChatModel.FromCatalog("does-not-exist", AcceptAll).EnsureReadyAsync());

        // A file whose bytes do not match the signed catalog hash is rejected and never installed.
        var corrupt = await Assert.ThrowsAsync<LocalAiException>(() => ChatModel.FromCatalog("test-corrupt-chat", AcceptAll).EnsureReadyAsync());
        Assert.Equal(LocalAiErrorCode.IntegrityFailure, corrupt.ErrorCode);
        Assert.Equal(ModelStatus.NeedsModel, await ChatModel.FromCatalog("test-corrupt-chat").GetStatusAsync());

        // Policy Never refuses to download.
        var never = new ModelOptions { AcquisitionPolicy = AcquisitionPolicy.Never, LicenseAcceptance = (_, _) => ValueTask.FromResult(true) };
        await Assert.ThrowsAsync<NotDownloadedException>(() => ChatModel.FromCatalog("test-other-chat", never).EnsureReadyAsync());

        // Declined consent under Prompt refuses to download.
        var declined = new ModelOptions { LicenseAcceptance = (_, _) => ValueTask.FromResult(true), DownloadConsent = (_, _) => ValueTask.FromResult(false) };
        await Assert.ThrowsAsync<NotDownloadedException>(() => ChatModel.FromCatalog("test-other-chat", declined).EnsureReadyAsync());

        // A model that is not downloaded cannot open a session.
        await Assert.ThrowsAsync<NotDownloadedException>(() => ChatModel.FromCatalog("test-other-chat").GetClientAsync());
    }

    [Fact]
    public async Task ConcurrentEnsure_SharesOneDownload()
    {
        string home = site.UseFreshHome();
        var tasks = Enumerable.Range(0, 4).Select(_ => ChatModel.FromCatalog("test-other-chat", AcceptAll).EnsureReadyAsync()).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(ModelStatus.Ready, await ChatModel.FromCatalog("test-other-chat").GetStatusAsync());
        Assert.Single(Directory.GetFiles(Path.Combine(home, "models", ".blobs")));
    }

    [Fact]
    public async Task SelfUpdate_InstallsNewRuntimeSideBySide_AndClientsMoveToIt()
    {
        const string channel = "test-update";
        const string next = "1.0.1-test";
        site.PublishRelease(channel, SiteFixture.RuntimeVersion);
        string home = site.UseFreshHome(channel,
            ("LOCALAI_UPDATE_INITIAL_DELAY_SECONDS", "1"),
            ("LOCALAI_UPDATE_INTERVAL_SECONDS", "1"),
            ("LOCALAI_BROKER_TICK_MS", "200"),
            ("LOCALAI_HOST_IDLE_SECONDS", "2"));

        var model = ChatModel.FromCatalog("test-echo-chat", AcceptAll);
        await model.EnsureReadyAsync();
        Assert.Equal("Echo: v1", (await (await model.GetClientAsync()).GetResponseAsync("v1")).Text);
        Assert.Equal(SiteFixture.RuntimeVersion, File.ReadAllText(Path.Combine(home, "runtime", "current.txt")).Trim());

        site.PackRuntime(next);
        site.PublishRelease(channel, next);

        string currentFile = Path.Combine(home, "runtime", "current.txt");
        await WaitUntilAsync(() => File.ReadAllText(currentFile).Trim() == next, TimeSpan.FromSeconds(30), home);

        // The old broker drains; the next call starts the new broker from runtime\<next>.
        string newRuntimeDir = Path.Combine(home, "runtime", next);
        await WaitUntilAsync(() => SiteFixture.ProcessesUnder(Path.Combine(home, "runtime", SiteFixture.RuntimeVersion), "broker").Count == 0, TimeSpan.FromSeconds(30), home);
        IChatClient client = await ChatModel.FromCatalog("test-echo-chat").GetClientAsync();
        Assert.Equal("Echo: v2", (await client.GetResponseAsync("v2")).Text);
        Assert.Single(SiteFixture.ProcessesUnder(newRuntimeDir, "broker"));
        Assert.Single(SiteFixture.ProcessesUnder(newRuntimeDir, "host"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string? home = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                string logs = home is null ? "" : string.Join("\n", Directory.EnumerateFiles(Path.Combine(home, "logs"), "*.log").Select(f => $"--- {Path.GetFileName(f)}\n{File.ReadAllText(f)}"));
                Assert.Fail("Timed out waiting for the condition.\n" + logs);
            }

            await Task.Delay(200);
        }
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
