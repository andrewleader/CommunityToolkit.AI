using System.Diagnostics;
using CommunityToolkit.AI.Protocol;
using CommunityToolkit.AI.Protocol.Runtime;
using CommunityToolkit.AI.Protocol.Trust;

namespace CommunityToolkit.AI.Broker;

/// <summary>
/// Self-update (spec 9): fetch and verify the manifest, install the new version side by side, health-check it,
/// flip current.txt. The running broker then drains and exits, and the next client connection starts the new one.
/// Rollback on a crash loop is done by the bootstrapper using lastKnownGood.
/// </summary>
internal sealed class Updater
{
    private readonly BrokerContext _ctx;
    private readonly CatalogService _catalog;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Updater(BrokerContext ctx, CatalogService catalog)
    {
        _ctx = ctx;
        _catalog = catalog;
    }

    public bool Disabled => _ctx.Policy.DisableUpdates;

    /// <summary>Runs the periodic check: 12-24 h with jitter, plus a check soon after start when the last one is old.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (Disabled)
        {
            Log.Info("Updates are disabled by policy.");
            return;
        }

        var state = UpdateState.Load(_ctx.Paths);
        TimeSpan initial = EnvSeconds("LOCALAI_UPDATE_INITIAL_DELAY_SECONDS") ??
            (state.LastCheck is null || DateTimeOffset.UtcNow - state.LastCheck > TimeSpan.FromHours(24) ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(12));
        try
        {
            await Task.Delay(initial, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                await CheckAsync(ct).ConfigureAwait(false);
                TimeSpan interval = EnvSeconds("LOCALAI_UPDATE_INTERVAL_SECONDS") ?? TimeSpan.FromHours(12 + Random.Shared.NextDouble() * 12);
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Checks once. Returns the version current.txt now points to when it changed, otherwise null.</summary>
    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        if (Disabled || !await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return null;
        }

        var state = UpdateState.Load(_ctx.Paths);
        try
        {
            var manifest = await _catalog.GetManifestAsync(refresh: true, ct).ConfigureAwait(false);
            state.LastCheck = DateTimeOffset.UtcNow;
            if (manifest is null)
            {
                state.LastResult = "manifest unavailable";
                return null;
            }

            string current = _ctx.Paths.ReadCurrentVersion() ?? _ctx.RuntimeVersion;
            bool forced = manifest.KillSwitch.Contains(current) || (manifest.MinVersion is { } min && SemVer.Compare(current, min) < 0);
            if (manifest.Version == current || manifest.KillSwitch.Contains(manifest.Version))
            {
                state.LastResult = "up to date";
                return null;
            }

            if (!forced && !_ctx.Installer.IsInRollout(manifest.RolloutPercent))
            {
                state.LastResult = $"{manifest.Version} not yet rolled out to this user";
                return null;
            }

            Log.Info($"Updating runtime {current} -> {manifest.Version}{(forced ? " (required)" : "")}.");
            string version = await _ctx.Installer.InstallAsync(manifest, null, ct).ConfigureAwait(false);
            if (!await HealthCheckAsync(version, ct).ConfigureAwait(false))
            {
                state.LastResult = $"{version} failed its health check";
                Telemetry.Record("runtime.update", ("from", current), ("to", version), ("result", "healthcheck-failed"));
                Log.Warn(state.LastResult);
                return null;
            }

            state.Previous = current;
            state.LastKnownGood ??= current;
            state.LastResult = $"updated to {version}";
            _ctx.Paths.WriteCurrentVersion(version);
            Telemetry.Record("runtime.update", ("from", current), ("to", version), ("result", "ok"));
            Log.Info($"current.txt now points to {version}.");
            return version;
        }
        catch (Exception ex) when (ex is TrustException or RpcException or IOException or HttpRequestException or InvalidDataException)
        {
            state.LastResult = $"failed: {ex.Message}";
            Telemetry.Record("runtime.update", ("result", ex is RpcException r ? r.Code : ex.GetType().Name));
            Log.Warn($"Update failed: {ex.Message}");
            return null;
        }
        finally
        {
            state.Save(_ctx.Paths);
            _gate.Release();
        }
    }

    /// <summary>Starts the new broker in health-check mode side by side and requires a clean exit.</summary>
    private async Task<bool> HealthCheckAsync(string version, CancellationToken ct)
    {
        string dir = _ctx.Paths.RuntimeVersionDir(version);
        try
        {
            using var process = ProcessLauncher.Start(Path.Combine(dir, "broker.exe"), ["--health-check", "--home", _ctx.Paths.Home], dir, breakawayFromJob: true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"Health check of {version} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Deletes runtime versions other than current, previous and last known good once they are old enough.</summary>
    public void CleanupOldRuntimes(TimeSpan grace)
    {
        var state = UpdateState.Load(_ctx.Paths);
        var keep = new HashSet<string?>(StringComparer.OrdinalIgnoreCase) { _ctx.Paths.ReadCurrentVersion(), state.LastKnownGood, state.Previous, _ctx.RuntimeVersion };
        if (!Directory.Exists(_ctx.Paths.RuntimeDir))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(_ctx.Paths.RuntimeDir))
        {
            string name = Path.GetFileName(dir);
            if (!keep.Contains(name) && DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > grace)
            {
                Log.Info($"Removing old runtime {name}.");
                RuntimeInstaller.TryDeleteDirectory(dir);
            }
        }
    }

    private static TimeSpan? EnvSeconds(string name) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.CultureInfo.InvariantCulture, out double s) ? TimeSpan.FromSeconds(s) : null;
}

/// <summary>Per-user integration: the daily update task and the Add/Remove Programs entry. Neither needs elevation.</summary>
internal static class SystemIntegration
{
    private const string TaskName = @"CommunityToolkit.AI\LocalAI Updater";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CommunityToolkit.AI.LocalAI";

    /// <summary>Writes runtime\localai.cmd, a stable launcher that follows current.txt.</summary>
    public static string WriteLauncher(LocalAIPaths paths)
    {
        string launcher = Path.Combine(paths.RuntimeDir, "localai.cmd");
        string script = "@echo off\r\nsetlocal\r\nset /p V=<\"%~dp0current.txt\"\r\n\"%~dp0%V%\\broker.exe\" %*\r\n";
        if (!File.Exists(launcher) || File.ReadAllText(launcher) != script)
        {
            AtomicFile.WriteAllText(launcher, script);
        }

        return launcher;
    }

    public static void Register(LocalAIPaths paths, string version)
    {
        string launcher = WriteLauncher(paths);
        string marker = Path.Combine(paths.StateDir, "integration.txt");
        if (File.Exists(marker))
        {
            return;
        }

        RunSchtasks(["/Create", "/F", "/SC", "DAILY", "/ST", "12:00", "/TN", TaskName, "/TR", $"\"{launcher}\" --update-once --home \"{paths.Home}\""]);
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UninstallKey);
            key.SetValue("DisplayName", "Local AI Runtime (CommunityToolkit.AI)");
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "CommunityToolkit.AI contributors");
            key.SetValue("InstallLocation", paths.Home);
            key.SetValue("UninstallString", $"\"{launcher}\" --uninstall --home \"{paths.Home}\"");
            key.SetValue("NoModify", 1);
            key.SetValue("NoRepair", 1);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Warn($"Could not write the uninstall entry: {ex.Message}");
        }

        AtomicFile.WriteAllText(marker, version);
    }

    public static void Unregister()
    {
        RunSchtasks(["/Delete", "/F", "/TN", TaskName]);
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }
    }

    private static void RunSchtasks(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi)!;
            process.WaitForExit(15_000);
            if (process.ExitCode != 0)
            {
                Log.Warn($"schtasks {args[0]} failed: {process.StandardError.ReadToEnd().Trim()}");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"schtasks failed: {ex.Message}");
        }
    }
}
