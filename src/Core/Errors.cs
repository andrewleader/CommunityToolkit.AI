using CommunityToolkit.AI.Protocol;

namespace CommunityToolkit.AI;

/// <summary>Stable error codes for <see cref="LocalAiException"/>.</summary>
public enum LocalAiErrorCode
{
    Unknown,
    NotDownloaded,
    RuntimeUnavailable,
    DeviceUnsupported,
    ModelUnknown,
    HostCrashed,
    Busy,
    LicenseNotAccepted,
    NotSupported,
    ProtocolTooOld,
    Offline,
    InsufficientDisk,
    IntegrityFailure,
}

/// <summary>Base type for every error raised by the Local AI runtime.</summary>
public class LocalAiException : Exception
{
    public LocalAiException(LocalAiErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = code;
    }

    public LocalAiErrorCode ErrorCode { get; }

    internal static Exception FromRpc(Exception ex, bool fromHost)
    {
        return ex switch
        {
            LocalAiException or OperationCanceledException => ex,
            RpcException rpc => FromCode(rpc.Code, rpc.Message, rpc),
            RpcConnectionLostException lost when fromHost => new HostCrashedException("The model host exited unexpectedly.", lost),
            RpcConnectionLostException lost => new RuntimeUnavailableException("The connection to the Local AI runtime was lost.", lost),
            _ => ex,
        };
    }

    internal static Exception FromCode(string code, string message, Exception? inner = null) => code switch
    {
        ErrorCodes.NotDownloaded => new NotDownloadedException(message, inner),
        ErrorCodes.RuntimeUnavailable => new RuntimeUnavailableException(message, inner),
        ErrorCodes.DeviceUnsupported => new DeviceUnsupportedException(message, inner),
        ErrorCodes.ModelUnknown => new ModelUnknownException(message, inner),
        ErrorCodes.HostCrashed => new HostCrashedException(message, inner),
        ErrorCodes.Busy => new BusyException(message, inner),
        ErrorCodes.LicenseNotAccepted => new LicenseNotAcceptedException(message, inner),
        ErrorCodes.NotSupported => new NotSupportedByRuntimeException(message, inner),
        ErrorCodes.ProtocolTooOld => new ProtocolTooOldException(message, inner),
        ErrorCodes.Offline => new OfflineException(message, inner),
        ErrorCodes.InsufficientDisk => new InsufficientDiskException(message, inner),
        ErrorCodes.IntegrityFailure => new LocalAiException(LocalAiErrorCode.IntegrityFailure, message, inner),
        ErrorCodes.Cancelled => new OperationCanceledException(message, inner),
        _ => new LocalAiException(LocalAiErrorCode.Unknown, message, inner),
    };
}

/// <summary>The model, runtime or execution provider is not downloaded and the policy did not allow a download.</summary>
public sealed class NotDownloadedException(string message, Exception? inner = null) : LocalAiException(LocalAiErrorCode.NotDownloaded, message, inner);

/// <summary>The runtime could not be installed, started or reached.</summary>
public sealed class RuntimeUnavailableException(string message, Exception? inner = null) : LocalAiException(LocalAiErrorCode.RuntimeUnavailable, message, inner);

/// <summary>No device on this machine can run the model.</summary>
public sealed class DeviceUnsupportedException(string message, Exception? inner = null) : LocalAiException(LocalAiErrorCode.DeviceUnsupported, message, inner);

/// <summary>The id is not in the catalog, or it is not a model for this task.</summary>
public sealed class ModelUnknownException(string message, Exception? inner = null) : LocalAiException(LocalAiErrorCode.ModelUnknown, message, inner);

/// <summary>The model host crashed. In-flight calls fail; the next call restarts the host.</summary>
public sealed class HostCrashedException(string message, Exception? inner = null) : LocalAiException(LocalAiErrorCode.HostCrashed, message, inner);

/// <summary>The runtime is too busy to accept the request.</summary>
public sealed class BusyException(string message, Exception? inner = null) : LocalAiException(LocalAiErrorCode.Busy, message, inner);

/// <summary>The model's licence has not been accepted.</summary>
public sealed class LicenseNotAcceptedException(string message, Exception? inner = null) : LocalAiException(LocalAiErrorCode.LicenseNotAccepted, message, inner);

/// <summary>The runtime does not support this operation (for example a peer that is older than the caller).</summary>
public sealed class NotSupportedByRuntimeException(string message, Exception? inner = null) : LocalAiException(LocalAiErrorCode.NotSupported, message, inner);

/// <summary>The installed runtime speaks an older protocol than this package needs and could not update.</summary>
public sealed class ProtocolTooOldException(string message, Exception? inner = null) : LocalAiException(LocalAiErrorCode.ProtocolTooOld, message, inner);

/// <summary>A download failed because the network is unavailable.</summary>
public sealed class OfflineException(string message, Exception? inner = null) : LocalAiException(LocalAiErrorCode.Offline, message, inner);

/// <summary>There is not enough disk space for the download.</summary>
public sealed class InsufficientDiskException(string message, Exception? inner = null) : LocalAiException(LocalAiErrorCode.InsufficientDisk, message, inner);
