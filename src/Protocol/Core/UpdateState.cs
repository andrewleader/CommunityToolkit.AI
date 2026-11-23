using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunityToolkit.AI.Protocol;

/// <summary>state\update.json: which runtime versions are known good, for rollback.</summary>
internal sealed class UpdateState
{
    [JsonPropertyName("lastKnownGood")] public string? LastKnownGood { get; set; }
    [JsonPropertyName("previous")] public string? Previous { get; set; }
    [JsonPropertyName("lastCheck")] public DateTimeOffset? LastCheck { get; set; }
    [JsonPropertyName("lastResult")] public string? LastResult { get; set; }

    /// <summary>Recent broker start failures, used for crash-loop detection.</summary>
    [JsonPropertyName("failures")] public List<LaunchFailure> Failures { get; set; } = new();

    public static UpdateState Load(LocalAIPaths paths)
    {
        try
        {
            if (File.Exists(paths.UpdateStateFile))
            {
                return JsonSerializer.Deserialize(File.ReadAllBytes(paths.UpdateStateFile), CoreJsonContext.Default.UpdateState) ?? new UpdateState();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }

        return new UpdateState();
    }

    public void Save(LocalAIPaths paths) =>
        AtomicFile.WriteAllBytes(paths.UpdateStateFile, JsonSerializer.SerializeToUtf8Bytes(this, CoreJsonContext.Default.UpdateState));

    /// <summary>Records a failure and returns true when the version is crash-looping (3 failures in 10 minutes).</summary>
    public bool RecordFailure(string version, DateTimeOffset now)
    {
        Failures.RemoveAll(f => now - f.At > TimeSpan.FromMinutes(10));
        Failures.Add(new LaunchFailure { Version = version, At = now });
        return Failures.Count(f => f.Version == version) >= 3;
    }
}

internal sealed class LaunchFailure
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("at")] public DateTimeOffset At { get; set; }
}

/// <summary>state\install-progress.json: lets processes waiting on the install lock show the installer's progress.</summary>
internal sealed class InstallProgress
{
    [JsonPropertyName("done")] public long Done { get; set; }
    [JsonPropertyName("total")] public long Total { get; set; }
}

/// <summary>
/// A cross-process lock backed by an exclusively opened file. The OS releases it if the holder dies,
/// and unlike a named mutex it can be held across awaits.
/// </summary>
internal sealed class FileLock : IDisposable
{
    private readonly FileStream _stream;

    private FileLock(FileStream stream) => _stream = stream;

    public static FileLock? TryAcquire(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new FileLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static async Task<FileLock> AcquireAsync(string path, Action? whileWaiting, CancellationToken ct)
    {
        while (true)
        {
            var acquired = TryAcquire(path);
            if (acquired is not null)
            {
                return acquired;
            }

            whileWaiting?.Invoke();
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    public void Dispose() => _stream.Dispose();
}
