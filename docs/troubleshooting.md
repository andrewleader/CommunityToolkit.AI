# Troubleshooting and errors

Logs are in `%LocalAppData%\CommunityToolkit\LocalAI\logs` (`broker-*.log`, `host-*.log`). They never contain prompts,
audio or images.

## Error reference

Every error derives from `LocalAiException` and has an `ErrorCode`.

| Exception | Code | Meaning and what to do |
|---|---|---|
| `NotDownloadedException` | `NotDownloaded` | The runtime or model is missing and the policy (or the user) did not allow a download. Call `EnsureReadyAsync` with `Prompt` or `Silent`. |
| `RuntimeUnavailableException` | `RuntimeUnavailable` | The runtime could not be installed, started or reached. Check the logs and AV quarantine. |
| `DeviceUnsupportedException` | `DeviceUnsupported` | No available device can run the model (memory or execution provider). |
| `ModelUnknownException` | `ModelUnknown` | The id is not in the catalog, or it is not a model for this task. |
| `HostCrashedException` | `HostCrashed` | The model host crashed. Retry: the next call restarts it. |
| `BusyException` | `Busy` | The runtime is overloaded. Retry later. |
| `LicenseNotAcceptedException` | `LicenseNotAccepted` | Set `ModelOptions.LicenseAcceptance` and let the user accept. |
| `NotSupportedByRuntimeException` | `NotSupported` | The peer does not support this operation or contract version. |
| `ProtocolTooOldException` | `ProtocolTooOld` | Your package needs a newer runtime. The runtime has been asked to update; retry shortly. |
| `OfflineException` | `Offline` | A download failed because the network is unavailable. Downloads resume when you retry. |
| `InsufficientDiskException` | `InsufficientDisk` | Free up disk space. |
| `LocalAiException` | `IntegrityFailure` | A downloaded file or manifest failed verification and was discarded. |

## Common problems

**Antivirus quarantined `broker.exe` or `host.exe`.** v1 binaries are unsigned and the runtime self-updates over a
named pipe, which some AV products flag. Restore the file or allow-list
`%LocalAppData%\CommunityToolkit\LocalAI\runtime`; the next `EnsureReadyAsync` re-verifies and repairs the install.

**Proxy or captive portal.** Downloads use the system proxy. A captive portal shows up as a signature or hash failure
(the portal's page is not our file) and nothing is installed. Sign in to the network and retry.

**Corporate network blocks Hugging Face.** Set the `HuggingFaceMirror` policy (see [Enterprise settings](enterprise.md)).

**Low disk space.** The runtime needs about 200 MB free; a model needs its size plus 10%.
