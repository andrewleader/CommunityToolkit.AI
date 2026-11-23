namespace CommunityToolkit.AI;

/// <summary>Where a model stands on this machine. Apps use it to build their UX.</summary>
public enum ModelStatus
{
    /// <summary>The runtime, execution provider and model are present and verified.</summary>
    Ready,

    /// <summary>The Local AI runtime is not installed yet.</summary>
    NeedsRuntime,

    /// <summary>The execution provider the model needs is not installed yet.</summary>
    NeedsEp,

    /// <summary>The model files are not downloaded yet.</summary>
    NeedsModel,

    /// <summary>This machine cannot run the model, or the model is not in the catalog for this task.</summary>
    Unsupported,

    /// <summary>A download for the runtime or the model is in progress.</summary>
    Downloading,
}

/// <summary>A preference for the device that runs the model. The runtime falls back when it is unavailable.</summary>
public enum Device
{
    Auto,
    CPU,
    GPU,
    NPU,
}

/// <summary>Controls whether <see cref="Model.EnsureReadyAsync"/> may download.</summary>
public enum AcquisitionPolicy
{
    /// <summary>Download after <see cref="ModelOptions.DownloadConsent"/> approves, if the app set it.</summary>
    Prompt,

    /// <summary>Download without asking for consent. Licence acceptance still applies.</summary>
    Silent,

    /// <summary>Never download. Missing pieces raise <see cref="NotDownloadedException"/>.</summary>
    Never,
}

/// <summary>The stage an <see cref="EnsureProgress"/> report belongs to.</summary>
public enum EnsureStage
{
    Runtime,
    Ep,
    Model,
    Verify,
}

/// <summary>Progress for <see cref="Model.EnsureReadyAsync"/>, carrying enough to drive a single progress bar.</summary>
/// <param name="Stage">The current stage.</param>
/// <param name="BytesDone">Bytes downloaded so far in this stage.</param>
/// <param name="BytesTotal">Total bytes for this stage, or 0 when unknown.</param>
/// <param name="OverallFraction">Overall completion from 0 to 1 across all stages.</param>
public readonly record struct EnsureProgress(EnsureStage Stage, long BytesDone, long BytesTotal, double OverallFraction);

/// <summary>A licence the user must accept before a model is downloaded.</summary>
/// <param name="ModelId">The catalog id of the model.</param>
/// <param name="LicenseId">The licence id, for example an SPDX id such as MIT.</param>
/// <param name="Url">Where the licence text can be read.</param>
public sealed record ModelLicense(string ModelId, string LicenseId, Uri? Url);

/// <summary>Describes a pending download so the app can ask the user for consent.</summary>
/// <param name="ModelId">The catalog id of the model.</param>
/// <param name="Bytes">Bytes that will be downloaded.</param>
public sealed record DownloadConsentRequest(string ModelId, long Bytes);

/// <summary>Per-model options. All of them are preferences or callbacks owned by the app.</summary>
public sealed class ModelOptions
{
    /// <summary>The preferred device. A preference only.</summary>
    public Device Device { get; set; } = Device.Auto;

    public AcquisitionPolicy AcquisitionPolicy { get; set; } = AcquisitionPolicy.Prompt;

    /// <summary>
    /// Called when the model's licence must be accepted. Return true if the user accepted it.
    /// Without a callback, an unaccepted licence raises <see cref="LicenseNotAcceptedException"/>.
    /// </summary>
    public Func<ModelLicense, CancellationToken, ValueTask<bool>>? LicenseAcceptance { get; set; }

    /// <summary>Called before a download under <see cref="AcquisitionPolicy.Prompt"/>. Return false to decline.</summary>
    public Func<DownloadConsentRequest, CancellationToken, ValueTask<bool>>? DownloadConsent { get; set; }
}
