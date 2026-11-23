using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using CommunityToolkit.AI.Protocol;
using CommunityToolkit.AI.Protocol.Trust;

namespace CommunityToolkit.AI;

/// <summary>
/// Gets a connection to the per-user broker, launching it or installing the runtime first when needed (spec section 7).
/// </summary>
internal sealed class Bootstrapper
{
    private static readonly TimeSpan BrokerStartTimeout = TimeSpan.FromSeconds(30);
    private readonly ClientEnvironment _env;

    public Bootstrapper(ClientEnvironment env) => _env = env;

    private LocalAIPaths Paths => _env.Paths;

    private string InstallLockPath => Path.Combine(Paths.StateDir, "install.lock");

    public async Task<Stream?> TryConnectAsync(int timeoutMs, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", Paths.BrokerPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
            return pipe;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>The installed runtime version, if current.txt points at a runtime whose broker passes a hash check.</summary>
    public string? GetInstalledVersion()
    {
        string? version = Paths.ReadCurrentVersion();
        return version is not null && RuntimeInstaller.VerifyRuntimeDirectory(Paths.RuntimeVersionDir(version), version, fullHash: false) ? version : null;
    }

    public bool IsInstallInProgress()
    {
        using var probe = FileLock.TryAcquire(InstallLockPath);
        return probe is null;
    }

    /// <param name="allowInstall">Whether the runtime may be downloaded.</param>
    /// <param name="runtimeProgress">Bytes done and total while the runtime downloads.</param>
    public async Task<Stream> ConnectAsync(bool allowInstall, Action<long, long>? runtimeProgress, CancellationToken ct)
    {
        if (await TryConnectAsync(200, ct).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        if (GetInstalledVersion() is { } installed && await LaunchWithRollbackAsync(installed, ct).ConfigureAwait(false) is { } launched)
        {
            return launched;
        }

        if (!allowInstall)
        {
            throw new NotDownloadedException("The Local AI runtime is not installed and the acquisition policy does not allow downloading it.");
        }

        using (await FileLock.AcquireAsync(InstallLockPath, () => ReportSharedProgress(runtimeProgress), ct).ConfigureAwait(false))
        {
            // Another process may have finished the install while we waited.
            if (await TryConnectAsync(200, ct).ConfigureAwait(false) is { } raced)
            {
                return raced;
            }

            if (GetInstalledVersion() is { } version && await LaunchWithRollbackAsync(version, ct).ConfigureAwait(false) is { } afterWait)
            {
                return afterWait;
            }

            string installedVersion = await InstallAsync(runtimeProgress, ct).ConfigureAwait(false);
            return await LaunchWithRollbackAsync(installedVersion, ct).ConfigureAwait(false)
                ?? throw new RuntimeUnavailableException($"Runtime {installedVersion} was installed but the broker did not start.");
        }
    }

    private async Task<string> InstallAsync(Action<long, long>? progress, CancellationToken ct)
    {
        using var http = SecureDownloader.CreateHttpClient($"CommunityToolkit.AI.Core/{typeof(Bootstrapper).Assembly.GetName().Version}");
        var installer = new RuntimeInstaller(Paths, _env.Config, _env.CreateTrustStore(), http, _env.DevMode);
        try
        {
            var manifest = await installer.FetchManifestAsync(ct).ConfigureAwait(false);
            var zip = manifest.RuntimeZip ?? throw new RuntimeUnavailableException("The release manifest has no runtime.");
            Directory.CreateDirectory(Paths.StagingDir);
            DiskSpace.EnsureAvailable(Paths.StagingDir, zip.Size * 3);

            long lastWrite = 0;
            string version = await installer.InstallAsync(manifest, (done, total) =>
            {
                progress?.Invoke(done, total);
                long now = Environment.TickCount64;
                if (now - lastWrite > 250 || done == total)
                {
                    lastWrite = now;
                    TryWriteSharedProgress(done, total);
                }
            }, ct).ConfigureAwait(false);

            Paths.WriteCurrentVersion(version);
            return version;
        }
        catch (HttpRequestException ex)
        {
            throw new OfflineException($"Could not download the Local AI runtime from {installer.RootUri}: {ex.Message}", ex);
        }
        catch (TrustException ex)
        {
            throw new LocalAiException(LocalAiErrorCode.IntegrityFailure, $"The runtime release could not be verified: {ex.Message}", ex);
        }
        catch (RpcException ex)
        {
            throw LocalAiException.FromCode(ex.Code, ex.Message, ex);
        }
        catch (InvalidDataException ex)
        {
            throw new LocalAiException(LocalAiErrorCode.IntegrityFailure, $"The runtime package is invalid: {ex.Message}", ex);
        }
        finally
        {
            RuntimeInstaller.TryDeleteFile(Paths.InstallStateFile);
        }
    }

    /// <summary>Launches a version; on a crash loop falls back to the last known good version.</summary>
    private async Task<Stream?> LaunchWithRollbackAsync(string version, CancellationToken ct)
    {
        var state = UpdateState.Load(Paths);
        if (await LaunchAsync(version, ct).ConfigureAwait(false) is { } stream)
        {
            if (state.LastKnownGood != version || state.Failures.Count > 0)
            {
                state.LastKnownGood = version;
                state.Failures.RemoveAll(f => f.Version == version);
                state.Save(Paths);
            }

            return stream;
        }

        bool looping = state.RecordFailure(version, DateTimeOffset.UtcNow);
        string? fallback = state.LastKnownGood;
        if (looping && fallback is not null && fallback != version &&
            RuntimeInstaller.VerifyRuntimeDirectory(Paths.RuntimeVersionDir(fallback), fallback, fullHash: false))
        {
            Trace.TraceWarning($"LocalAI: runtime {version} is crash-looping; rolling back to {fallback}.");
            state.Previous = version;
            state.LastResult = $"rolled back from {version}";
            state.Save(Paths);
            Paths.WriteCurrentVersion(fallback);
            return await LaunchAsync(fallback, ct).ConfigureAwait(false);
        }

        state.Save(Paths);
        return null;
    }

    private async Task<Stream?> LaunchAsync(string version, CancellationToken ct)
    {
        string dir = Paths.RuntimeVersionDir(version);
        string exe = Path.Combine(dir, "broker.exe");
        WriteConfig();

        Process process;
        try
        {
            process = ProcessLauncher.Start(exe, ["--home", Paths.Home], dir, breakawayFromJob: true);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Trace.TraceWarning($"LocalAI: could not start {exe}: {ex.Message}");
            return null;
        }

        using (process)
        {
            var deadline = DateTime.UtcNow + BrokerStartTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await TryConnectAsync(250, ct).ConfigureAwait(false) is { } stream)
                {
                    return stream;
                }

                // Exit code 0 means another broker owns the singleton, so keep trying to connect to it.
                if (process.HasExited && process.ExitCode != 0)
                {
                    Trace.TraceWarning($"LocalAI: broker {version} exited with code {process.ExitCode}.");
                    return null;
                }
            }
        }

        return null;
    }

    private void WriteConfig()
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(_env.Config, CoreJsonContext.Default.RuntimeConfig);
        try
        {
            if (!File.Exists(Paths.ConfigFile) || !File.ReadAllBytes(Paths.ConfigFile).AsSpan().SequenceEqual(json))
            {
                AtomicFile.WriteAllBytes(Paths.ConfigFile, json);
            }
        }
        catch (IOException)
        {
        }
    }

    private void TryWriteSharedProgress(long done, long total)
    {
        try
        {
            AtomicFile.WriteAllBytes(Paths.InstallStateFile, JsonSerializer.SerializeToUtf8Bytes(new InstallProgress { Done = done, Total = total }, CoreJsonContext.Default.InstallProgress));
        }
        catch (IOException)
        {
        }
    }

    private void ReportSharedProgress(Action<long, long>? progress)
    {
        if (progress is null)
        {
            return;
        }

        try
        {
            if (File.Exists(Paths.InstallStateFile) &&
                JsonSerializer.Deserialize(File.ReadAllBytes(Paths.InstallStateFile), CoreJsonContext.Default.InstallProgress) is { } shared)
            {
                progress(shared.Done, shared.Total);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }
    }
}
