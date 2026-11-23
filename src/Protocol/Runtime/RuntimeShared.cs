using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunityToolkit.AI.Protocol.Runtime;

/// <summary>Host process exit codes the broker understands.</summary>
internal static class HostExitCodes
{
    public const int Ok = 0;
    public const int BadArguments = 2;
    public const int UnsupportedAdapter = 10;
    public const int ModelLoadFailed = 11;
}

/// <summary>Broker process exit codes the bootstrapper understands.</summary>
internal static class BrokerExitCodes
{
    /// <summary>Another broker is serving; connect to it.</summary>
    public const int AlreadyRunning = 0;
    public const int Error = 1;
    public const int HealthCheckFailed = 4;
}

/// <summary>Task contract versions this runtime build serves, one entry per major (its max minor).</summary>
internal static class SupportedContracts
{
    public static readonly IReadOnlyDictionary<string, string[]> ByTask = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["chat"] = ["1.0"],
        ["ocr"] = ["0.1"],
        ["speech-to-text"] = ["0.1"],
    };

    public static string? Negotiate(string task, string clientMax, out string? errorCode)
    {
        if (!ByTask.TryGetValue(task, out var supported))
        {
            errorCode = ErrorCodes.ProtocolTooOld;
            return null;
        }

        return ContractVersions.Negotiate(clientMax, supported, out errorCode);
    }
}

/// <summary>
/// Content-free telemetry (spec 13): install and update outcomes, failure codes, model and EP ids, latency buckets, crash counts.
/// Events are written locally to logs\telemetry-*.jsonl; there is no upload endpoint in v1.
/// </summary>
internal static class Telemetry
{
    private static readonly object s_lock = new();
    private static string? s_dir;

    public static void Initialize(string logsDir, bool disabled)
    {
        bool optOut = disabled || Environment.GetEnvironmentVariable("LOCALAI_TELEMETRY_OPTOUT") is "1" or "true";
        s_dir = optOut ? null : logsDir;
    }

    public static void Record(string name, params (string Key, string? Value)[] properties)
    {
        if (s_dir is null)
        {
            return;
        }

        var evt = new TelemetryEvent { Name = name, Time = DateTimeOffset.UtcNow, Properties = properties.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!) };
        string line = JsonSerializer.Serialize(evt, RuntimeJsonContext.Default.TelemetryEvent) + Environment.NewLine;
        lock (s_lock)
        {
            try
            {
                File.AppendAllText(Path.Combine(s_dir, $"telemetry-{DateTime.UtcNow:yyyyMMdd}.jsonl"), line);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Buckets a latency so exact timings are never recorded.</summary>
    public static string LatencyBucket(TimeSpan elapsed) => elapsed.TotalMilliseconds switch
    {
        < 100 => "<100ms",
        < 500 => "<500ms",
        < 2000 => "<2s",
        < 10000 => "<10s",
        < 60000 => "<60s",
        _ => ">=60s",
    };
}

internal sealed class TelemetryEvent
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("time")] public DateTimeOffset Time { get; set; }
    [JsonPropertyName("props")] public Dictionary<string, string> Properties { get; set; } = new();
}

/// <summary>state\licenses.json: model id to accepted licence id.</summary>
internal sealed class LicenseState
{
    [JsonPropertyName("accepted")] public Dictionary<string, string> Accepted { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(TelemetryEvent))]
[JsonSerializable(typeof(LicenseState))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class RuntimeJsonContext : JsonSerializerContext
{
}
