using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunityToolkit.AI.Protocol;

internal static class CoreMethods
{
    public const string Hello = "core.hello";
    public const string ModelStatus = "core.modelStatus";
    public const string Ensure = "core.ensure";
    public const string OpenSession = "core.openSession";
    public const string Shutdown = "core.shutdown";

    /// <summary>First call on a host connection.</summary>
    public const string HostHello = "host.hello";
}

internal static class ProtocolInfo
{
    /// <summary>The core protocol version spoken by this build. The major is part of the pipe name.</summary>
    public const string CoreVersion = "1.0";
    public const int CoreMajor = 1;
}

internal sealed class HelloRequest
{
    [JsonPropertyName("clientName")] public string? ClientName { get; set; }
    [JsonPropertyName("processId")] public int ProcessId { get; set; }
    [JsonPropertyName("coreMaxVersion")] public string CoreMaxVersion { get; set; } = ProtocolInfo.CoreVersion;

    /// <summary>Task id to the max contract version the client speaks, for example chat = 1.0.</summary>
    [JsonPropertyName("tasks")] public Dictionary<string, string> Tasks { get; set; } = new();
}

internal sealed class HelloResponse
{
    [JsonPropertyName("coreVersion")] public string CoreVersion { get; set; } = ProtocolInfo.CoreVersion;
    [JsonPropertyName("runtimeVersion")] public string RuntimeVersion { get; set; } = "";
    [JsonPropertyName("tasks")] public Dictionary<string, string> Tasks { get; set; } = new();
    [JsonPropertyName("capabilities")] public List<string> Capabilities { get; set; } = new();

    /// <summary>Negotiated task contracts that are deprecated; the client logs a warning.</summary>
    [JsonPropertyName("deprecated")] public List<string> Deprecated { get; set; } = new();
}

internal sealed class ModelRequest
{
    [JsonPropertyName("modelId")] public string ModelId { get; set; } = "";
    [JsonPropertyName("task")] public string Task { get; set; } = "";
    [JsonPropertyName("device")] public string Device { get; set; } = "Auto";
}

internal sealed class LicenseDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("requiresAcceptance")] public bool RequiresAcceptance { get; set; }
    [JsonPropertyName("accepted")] public bool Accepted { get; set; }
}

internal sealed class ModelStatusResponse
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("modelVersion")] public string? ModelVersion { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("license")] public LicenseDto? License { get; set; }

    /// <summary>Bytes still to download before the model is ready.</summary>
    [JsonPropertyName("downloadBytes")] public long DownloadBytes { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

internal sealed class EnsureRequest
{
    [JsonPropertyName("modelId")] public string ModelId { get; set; } = "";
    [JsonPropertyName("task")] public string Task { get; set; } = "";
    [JsonPropertyName("device")] public string Device { get; set; } = "Auto";
    [JsonPropertyName("policy")] public string Policy { get; set; } = "Prompt";

    /// <summary>A licence id the user accepted in this call, recorded by the broker.</summary>
    [JsonPropertyName("acceptedLicenseId")] public string? AcceptedLicenseId { get; set; }
}

internal sealed class ProgressEvent
{
    [JsonPropertyName("stage")] public string Stage { get; set; } = "";
    [JsonPropertyName("bytesDone")] public long BytesDone { get; set; }
    [JsonPropertyName("bytesTotal")] public long BytesTotal { get; set; }
    [JsonPropertyName("fraction")] public double Fraction { get; set; }
}

internal sealed class EnsureResponse
{
    [JsonPropertyName("modelVersion")] public string ModelVersion { get; set; } = "";
}

internal sealed class OpenSessionRequest
{
    [JsonPropertyName("modelId")] public string ModelId { get; set; } = "";
    [JsonPropertyName("task")] public string Task { get; set; } = "";
    [JsonPropertyName("device")] public string Device { get; set; } = "Auto";
    [JsonPropertyName("contractVersion")] public string ContractVersion { get; set; } = "";

    /// <summary>A host process the client lost its connection to. The broker must not hand it out again.</summary>
    [JsonPropertyName("failedHostProcessId")] public int? FailedHostProcessId { get; set; }
}

internal sealed class OpenSessionResponse
{
    [JsonPropertyName("hostPipe")] public string HostPipe { get; set; } = "";
    [JsonPropertyName("modelVersion")] public string ModelVersion { get; set; } = "";
    [JsonPropertyName("ep")] public string Ep { get; set; } = "";
    [JsonPropertyName("hostProcessId")] public int HostProcessId { get; set; }
}

internal sealed class HostHelloRequest
{
    [JsonPropertyName("task")] public string Task { get; set; } = "";
    [JsonPropertyName("contractVersion")] public string ContractVersion { get; set; } = "";
    [JsonPropertyName("clientProcessId")] public int ClientProcessId { get; set; }
}

internal sealed class HostHelloResponse
{
    [JsonPropertyName("modelId")] public string ModelId { get; set; } = "";
    [JsonPropertyName("modelVersion")] public string ModelVersion { get; set; } = "";
    [JsonPropertyName("ep")] public string Ep { get; set; } = "";
    [JsonPropertyName("contractVersion")] public string ContractVersion { get; set; } = "";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(WireError))]
[JsonSerializable(typeof(HelloRequest))]
[JsonSerializable(typeof(HelloResponse))]
[JsonSerializable(typeof(ModelRequest))]
[JsonSerializable(typeof(ModelStatusResponse))]
[JsonSerializable(typeof(LicenseDto))]
[JsonSerializable(typeof(EnsureRequest))]
[JsonSerializable(typeof(ProgressEvent))]
[JsonSerializable(typeof(EnsureResponse))]
[JsonSerializable(typeof(OpenSessionRequest))]
[JsonSerializable(typeof(OpenSessionResponse))]
[JsonSerializable(typeof(HostHelloRequest))]
[JsonSerializable(typeof(HostHelloResponse))]
[JsonSerializable(typeof(RuntimeConfig))]
[JsonSerializable(typeof(UpdateState))]
[JsonSerializable(typeof(InstallProgress))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class CoreJsonContext : JsonSerializerContext
{
}
