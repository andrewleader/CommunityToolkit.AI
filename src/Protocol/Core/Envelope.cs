using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunityToolkit.AI.Protocol;

/// <summary>Envelope kinds on the wire.</summary>
internal static class EnvelopeKind
{
    public const string Request = "req";
    public const string Response = "res";
    public const string Event = "evt";
    public const string Error = "err";
    public const string Credit = "credit";
    public const string Cancel = "cancel";
}

/// <summary>
/// Every frame carries one envelope. Fields are additive only and unknown fields are ignored.
/// </summary>
internal sealed class Envelope
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("k")] public string Kind { get; set; } = EnvelopeKind.Request;
    [JsonPropertyName("m")] public string? Method { get; set; }
    [JsonPropertyName("p")] public JsonElement? Payload { get; set; }
    [JsonPropertyName("e")] public WireError? Error { get; set; }

    /// <summary>On a request: initial stream credits. On a credit envelope: credits granted.</summary>
    [JsonPropertyName("c")] public int? Credits { get; set; }
}

internal sealed class WireError
{
    [JsonPropertyName("code")] public string Code { get; set; } = ErrorCodes.Internal;
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("data")] public JsonElement? Data { get; set; }
}

/// <summary>Stable error codes. They map 1:1 to the public LocalAiException types.</summary>
internal static class ErrorCodes
{
    public const string NotDownloaded = "NotDownloaded";
    public const string RuntimeUnavailable = "RuntimeUnavailable";
    public const string DeviceUnsupported = "DeviceUnsupported";
    public const string ModelUnknown = "ModelUnknown";
    public const string HostCrashed = "HostCrashed";
    public const string Busy = "Busy";
    public const string LicenseNotAccepted = "LicenseNotAccepted";
    public const string NotSupported = "NotSupported";
    public const string ProtocolTooOld = "ProtocolTooOld";
    public const string Offline = "Offline";
    public const string InsufficientDisk = "InsufficientDisk";
    public const string Cancelled = "Cancelled";
    public const string InvalidRequest = "InvalidRequest";
    public const string IntegrityFailure = "IntegrityFailure";
    public const string Internal = "Internal";
}

/// <summary>Thrown by request handlers to return a typed error to the peer.</summary>
internal sealed class RpcException : Exception
{
    public RpcException(string code, string message, JsonElement? data = null)
        : base(message)
    {
        Code = code;
        ErrorData = data;
    }

    public string Code { get; }

    public JsonElement? ErrorData { get; }
}
