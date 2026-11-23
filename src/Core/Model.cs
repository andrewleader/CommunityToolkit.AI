using System.Text.Json;
using CommunityToolkit.AI.Protocol;

namespace CommunityToolkit.AI;

/// <summary>
/// Base class of every task model (chat, OCR, speech). It owns acquisition, so task packages only add their typed client.
/// Members are only ever added to this class, never changed or removed.
/// </summary>
public abstract class Model
{
    private const double RuntimeWeight = 0.15;

    /// <param name="id">The catalog id, for example qwen-2.5-1.5b-instruct.</param>
    /// <param name="taskId">The task id, for example chat.</param>
    protected Model(string id, string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        if (!LocalAIPaths.IsSafeName(id))
        {
            throw new ModelUnknownException($"'{id}' is not a valid catalog id.");
        }

        Id = id;
        TaskId = taskId;
    }

    /// <summary>The catalog id, for example qwen-2.5-1.5b-instruct.</summary>
    public string Id { get; }

    /// <summary>The task this model serves, for example chat.</summary>
    public string TaskId { get; }

    /// <summary>Device preference, acquisition policy and the licence and consent callbacks.</summary>
    public ModelOptions Options { get; init; } = new();

    /// <summary>Reports where the model stands. Never downloads anything, but may start the installed runtime.</summary>
    public async Task<ModelStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var broker = BrokerClient.Get();
        if (!broker.IsConnected)
        {
            if (await broker.Bootstrapper.TryConnectAsync(200, ct).ConfigureAwait(false) is { } probe)
            {
                await probe.DisposeAsync().ConfigureAwait(false);
            }
            else if (broker.Bootstrapper.GetInstalledVersion() is null)
            {
                return broker.Bootstrapper.IsInstallInProgress() ? ModelStatus.Downloading : ModelStatus.NeedsRuntime;
            }
        }

        try
        {
            var status = await QueryStatusAsync(broker, allowInstall: false, ct).ConfigureAwait(false);
            return Enum.TryParse<ModelStatus>(status.Status, out var parsed) ? parsed : ModelStatus.Unsupported;
        }
        catch (NotDownloadedException)
        {
            return ModelStatus.NeedsRuntime;
        }
        catch (RuntimeUnavailableException)
        {
            return ModelStatus.NeedsRuntime;
        }
    }

    /// <summary>
    /// Idempotent. Acquires and verifies the runtime, the execution provider and the model, honouring <see cref="ModelOptions"/>.
    /// </summary>
    public async Task EnsureReadyAsync(IProgress<EnsureProgress>? progress = null, CancellationToken ct = default)
    {
        var broker = BrokerClient.Get();
        bool allowDownload = Options.AcquisitionPolicy != AcquisitionPolicy.Never;
        bool installedRuntime = false;
        await broker.GetConnectionAsync(allowDownload, (done, total) =>
        {
            installedRuntime = true;
            double fraction = total > 0 ? (double)done / total : 0;
            progress?.Report(new EnsureProgress(EnsureStage.Runtime, done, total, RuntimeWeight * fraction));
        }, ct).ConfigureAwait(false);

        double baseline = installedRuntime ? RuntimeWeight : 0;
        var status = await QueryStatusAsync(broker, allowInstall: allowDownload, ct).ConfigureAwait(false);
        if (status.Status == nameof(ModelStatus.Unsupported))
        {
            throw new DeviceUnsupportedException(status.Reason ?? $"'{Id}' cannot run on this device.");
        }

        if (status.Status == nameof(ModelStatus.Ready))
        {
            progress?.Report(new EnsureProgress(EnsureStage.Verify, 0, 0, 1));
            return;
        }

        string? acceptedLicense = null;
        if (status.License is { RequiresAcceptance: true, Accepted: false } license)
        {
            var info = new ModelLicense(Id, license.Id, Uri.TryCreate(license.Url, UriKind.Absolute, out var url) ? url : null);
            if (Options.LicenseAcceptance is null || !await Options.LicenseAcceptance(info, ct).ConfigureAwait(false))
            {
                throw new LicenseNotAcceptedException($"The licence '{license.Id}' for '{Id}' has not been accepted.");
            }

            acceptedLicense = license.Id;
        }

        if (status.DownloadBytes > 0)
        {
            if (Options.AcquisitionPolicy == AcquisitionPolicy.Never)
            {
                throw new NotDownloadedException($"'{Id}' is not downloaded and the acquisition policy is Never.");
            }

            if (Options.AcquisitionPolicy == AcquisitionPolicy.Prompt && Options.DownloadConsent is { } consent &&
                !await consent(new DownloadConsentRequest(Id, status.DownloadBytes), ct).ConfigureAwait(false))
            {
                throw new NotDownloadedException($"The download of '{Id}' was declined.");
            }
        }

        var request = new EnsureRequest
        {
            ModelId = Id,
            Task = TaskId,
            Device = Options.Device.ToString(),
            Policy = Options.AcquisitionPolicy.ToString(),
            AcceptedLicenseId = acceptedLicense,
        };

        var connection = await broker.GetConnectionAsync(allowDownload, null, ct).ConfigureAwait(false);
        try
        {
            await using var call = await connection.StartCallAsync(CoreMethods.Ensure, JsonSerializer.SerializeToElement(request, CoreJsonContext.Default.EnsureRequest), initialCredits: 64, ct).ConfigureAwait(false);
            await foreach (var evt in call.ReadEventsAsync(ct).ConfigureAwait(false))
            {
                if (progress is not null && evt.Deserialize(CoreJsonContext.Default.ProgressEvent) is { } p)
                {
                    var stage = Enum.TryParse<EnsureStage>(p.Stage, out var s) ? s : EnsureStage.Model;
                    progress.Report(new EnsureProgress(stage, p.BytesDone, p.BytesTotal, baseline + (1 - baseline) * Math.Clamp(p.Fraction, 0, 1)));
                }
            }

            await call.GetResultAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw LocalAiException.FromRpc(ex, fromHost: false);
        }

        progress?.Report(new EnsureProgress(EnsureStage.Verify, 0, 0, 1));
    }

    /// <summary>
    /// For task packages: finds or spawns the model host and returns a session-bound connection to it.
    /// The model must be ready. The connection restarts the host lazily if it crashes.
    /// </summary>
    /// <param name="contractMaxVersion">The highest version of this task's contract the package speaks.</param>
    protected async Task<ModelHostConnection> ConnectToHostAsync(string contractMaxVersion, CancellationToken ct = default)
    {
        BrokerClient.RegisterTaskContract(TaskId, contractMaxVersion);
        var connection = new ModelHostConnection(this, contractMaxVersion);
        await connection.ReconnectAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private Task<ModelStatusResponse> QueryStatusAsync(BrokerClient broker, bool allowInstall, CancellationToken ct) =>
        broker.CallAsync(
            CoreMethods.ModelStatus,
            new ModelRequest { ModelId = Id, Task = TaskId, Device = Options.Device.ToString() },
            CoreJsonContext.Default.ModelRequest,
            CoreJsonContext.Default.ModelStatusResponse,
            allowInstall,
            ct);
}
