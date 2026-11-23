# Local AI Runtime for Windows: Spec and Plan

Status: Draft v0.5. The API samples are design mockups, not a shipping SDK.

Changes in v0.5: model files stay on Hugging Face and the signed catalog points at revision-pinned URLs with hashes (sections 10.1, 15.2, 18.1). Releases assets are no longer used for models.

## 1. Goals

- Give any Windows app, packaged or unpackaged, a clean API for local models (chat, OCR, speech-to-text, and more).
- The app-side footprint is 1-2 MB at most. It has no model code, no ONNX Runtime and no Windows ML.
- All inference runs out-of-process. A model crash never crashes the app.
- Apps that use the same model share one host process.
- Models, execution providers (EPs) and the runtime are downloaded on demand into shared per-user locations.
- The runtime self-updates without app updates.
- Windows ML is an implementation detail and is not exposed in the API.
- The product ships outside Windows and outside Windows App SDK.

## 2. Non-goals (v1)

- MSIX, MSI and framework packages. Flat binaries only.
- Per-machine (all-users) install. It is deferred because it needs signed binaries and elevation.
- Training and fine-tuning.
- Cloud fallback.
- Authenticode signing of our own binaries. The design must be signing-ready (section 11).

## 3. Public API (app-facing)

Namespaces are `CommunityToolkit.AI.<Task>`, and shared types live in `CommunityToolkit.AI`. The tasks are `Chat`, `SpeechToText` and `Ocr`. Each task ships as its own NuGet package (section 4.1).

```csharp
using CommunityToolkit.AI.Chat;
```

```csharp
ChatModel model = ChatModel.FromCatalog("qwen-2.5-1.5b-instruct"); // local, no I/O
await model.EnsureReadyAsync(progress, ct);                        // runtime + EP + model
IChatClient client = await model.GetClientAsync();                 // Microsoft.Extensions.AI
string text = await client.GetResponseAsync(prompt);
```

Every model type derives from a single abstract `Model` base class in `Core`. It owns everything about acquisition, so each task package adds only its typed client.

```csharp
namespace CommunityToolkit.AI;

public abstract class Model
{
    public string Id { get; }
    public Task<ModelStatus> GetStatusAsync(CancellationToken ct = default);
    public Task EnsureReadyAsync(IProgress<EnsureProgress>? progress = null, CancellationToken ct = default);
}
```

| Member | Defined on | Behavior |
|---|---|---|
| `Id` | `Model` | The catalog id, for example `qwen-2.5-1.5b-instruct`. |
| `GetStatusAsync()` | `Model` | Returns `Ready`, `NeedsRuntime`, `NeedsEp`, `NeedsModel`, `Unsupported`, or `Downloading`. Apps use this to build UX. |
| `EnsureReadyAsync(IProgress<EnsureProgress>, CancellationToken)` | `Model` | Idempotent. It acquires the runtime, the EP and the model, and verifies each. It is task-agnostic, so it lives only in `Core`. |
| `FromCatalog(id)` | each task model (static) | Returns a handle only and does no I/O. It validates that the id is a model for that task, and a mismatch is a `ModelUnknown` error. |
| `GetClientAsync(options)` | each task model | Finds or spawns the host and returns that task's session-bound proxy client. Its return type and options are task-specific, so it is not on the base class. |

Examples: `ChatModel : Model`, `SpeechToTextModel : Model` and `OcrModel : Model`.

The base class is part of `Core`, which is additive-only, so adding members to `Model` later must never break derived task packages. Its constructor is `protected` and takes only the catalog id and the task id.

`EnsureProgress` carries a stage (`Runtime`, `Ep`, `Model`, `Verify`), bytes done and total, and an overall fraction, so apps can show one bar.

Options: `Device = Auto | CPU | GPU | NPU` (a preference only), and `AcquisitionPolicy = Prompt | Silent | Never`. Licence acceptance is surfaced via a callback.

Typed errors (all derive from `LocalAiException`): `NotDownloaded`, `RuntimeUnavailable`, `DeviceUnsupported`, `ModelUnknown`, `HostCrashed`, `Busy`, `LicenseNotAccepted`, `NotSupported`, `ProtocolTooOld`, `Offline`, `InsufficientDisk`.

Chat uses `Microsoft.Extensions.AI` (`IChatClient`), with streaming via `GetStreamingResponseAsync`. Only the abstractions package is referenced.

## 4. Architecture

```
App process                     Broker (1 per user)               Model hosts (N)
+---------------------+  pipe   +--------------------------+ spawn +----------------------+
| Client library      |<------->| Catalog, store, updater, |<----->| host.exe             |
| API + RPC stub      |         | EP manager, host manager |       | Windows ML + ORT     |
| Bootstrapper        |         +--------------------------+       | task adapter         |
+---------------------+                                            +----------------------+
```

### 4.1 Client libraries (in app, ~1 MB)

The client is split into one **core** package plus **one package per task**, so a breaking change in one task never forces an app to move another.

| Package | Namespace | Contents | Depends on |
|---|---|---|---|
| `CommunityToolkit.AI.Core` | `CommunityToolkit.AI` | Pipe transport, handshake, the bootstrapper, the abstract `Model` base class (`EnsureReadyAsync`, `GetStatusAsync`), `ModelStatus`, `EnsureProgress`, `LocalAiException` and the shared options (`Device`, `AcquisitionPolicy`). No task knowledge. | none |
| `CommunityToolkit.AI.Chat` | `CommunityToolkit.AI.Chat` | `ChatModel`, chat options and the `IChatClient` adapter | `Core`, `Microsoft.Extensions.AI.Abstractions` |
| `CommunityToolkit.AI.SpeechToText` | `CommunityToolkit.AI.SpeechToText` | `SpeechToTextModel`, client and options | `Core` |
| `CommunityToolkit.AI.Ocr` | `CommunityToolkit.AI.Ocr` | `OcrModel`, client and result types | `Core` |

Rules:
- A task package contains only its typed API and its task messages. It uses `Core` for everything else.
- Each package is **versioned independently** with SemVer. A breaking change to Chat is `Chat` 2.0.0, while `Ocr` stays on 1.x. An app can reference `Chat` 2.x and `Ocr` 1.x together.
- `Core` is the shared dependency, so it is the most conservative. It changes additively only and stays on major version 1 for as long as possible. Task packages depend on a range, such as `Core` `[1.0.0, 2.0.0)`.
- If `Core` ever needs a major version, task packages that must work with both ship builds targeting each `Core` major, and the docs call this out. Treat it as a rare, planned event.
- No task package references another task package, and the only types shared across packages are the ones in `Core` (`Model`, status, progress, errors and shared options). Input and result types such as images and audio belong to the task package that uses them. If two tasks need a similar type, each defines its own.
- A task package never depends on the runtime version. It speaks only that task's wire contract (section 5).
- No package identity requirement. .NET is the primary target and must be AOT/trim friendly. A C ABI that exports the same functions for C++, Rust and JS projections is planned for later, and it will follow the same per-task split.
- Size budget: `Core` at most 1 MB and each task package at most 500 KB, so Core plus any one task is under 2 MB. CI fails a package that exceeds its budget.

### 4.2 Broker (per user, shared)

- One singleton per user, enforced by a named mutex.
- Serves a per-user named pipe, `\\.\pipe\LocalAI-<userSid>-v<protoMajor>`. The ACL allows the current user only.
- Responsibilities:
  - Catalog resolution.
  - Download manager.
  - Model store.
  - EP manager.
  - Host manager.
  - Updater.
  - Per-client session accounting.
  - Telemetry (opt-out, no content).
- It idles out after about 10 minutes with no clients, unless the update task has started it.

### 4.3 Model hosts

- One process per (model id, version, EP). All apps share it.
- It loads Windows ML and ONNX Runtime and runs a task adapter:
  - **Chat:** onnxruntime-genai with a tokenizer, a per-session KV cache and streaming.
  - **OCR:** image to text and boxes.
  - **Speech:** chunking, features and a decode loop.
- Sandbox:
  - Restricted token or AppContainer, with no network access.
  - Read-only access to the model store.
  - A job object enforcing memory and CPU limits and kill-on-close.
- Lifecycle:
  - Idle eviction after a timeout (default 5 minutes).
  - Memory-pressure eviction.
  - A crashed host triggers `HostCrashed` for in-flight calls, then a lazy restart.
- Scheduling: a per-model fair queue with a foreground app priority bonus. A session is isolated, with no cache sharing across apps.

### 4.4 Data transport

- The control plane is length-prefixed messages on the pipe.
- The data plane uses shared memory sections or duplicated handles for audio and images. Large payloads are never copied through the pipe.
- The client opens files and duplicates the handle. A host never opens paths supplied by a client.
- Streaming results are chunked frames with credit-based backpressure.

## 5. Wire protocol (frozen contract)

- Messages are protobuf or a similar schema-based, additive-only encoding.
- The protocol is layered so that tasks version independently:
  - **Core protocol** (owned by `Core`): connection, handshake, sessions, acquisition and progress, and errors. It is the most frozen layer.
  - **Task contracts** (one per task, owned by that task package): the messages for chat, speech or OCR, each with its own `major.minor` version.
- Handshake: the client sends its core `maxVersion` and, per task it uses, that task contract's `maxVersion`. The broker returns the negotiated versions and its capabilities.
- The broker and the hosts serve **multiple versions of each task contract side by side**. An app on `Chat` 1.x and an app on `Chat` 2.x can share the same runtime, and an app can mix `Chat` 2.x with `Ocr` 1.x.
- Rules:
  - Within a contract major version, never remove or renumber fields, and ignore unknown fields.
  - New operations return `NotSupported` to older peers.
  - A breaking change to a task creates a new task contract major version (and a new task package major version). The old major keeps being served by the runtime until it is deprecated.
  - A breaking change to the core protocol means a new pipe name with a major version, and the broker serves both.
- Deprecation: a task contract major version is supported for **12 months** after its successor ships. The runtime manifest can mark a contract as deprecated, and the client then logs a warning.
- Model hosts are shared across contract versions. A host adapter translates every supported contract version into one internal model API, so supporting an old contract costs an adapter shim, not a second host.
- CI has a compatibility matrix: every released version of each package runs against every runtime build, including mixed combinations (for example `Chat` 1.x with `Ocr` 2.x).

## 6. Install layout (per user, no elevation)

```
%LocalAppData%\CommunityToolkit\LocalAI\
  runtime\<version>\       broker.exe, host.exe, DLLs, files.json
  runtime\current.txt      pointer (atomic replace)
  models\<id>\<ver>\       content-addressed files + model.json
  staging\                 in-progress downloads
  state\                   install lock, update state, last-known-good
  logs\
```

- Writes are staged, verified, then renamed into place.
- Only the user can write these folders. Hosts get read-only access to `eps\` and `models\`.

## 7. Bootstrap flow (`EnsureReadyAsync`)

1. Try to connect to the pipe. If the broker is up, skip to step 6.
2. If `current.txt` points to a valid runtime, launch `broker.exe`, wait for the pipe, then go to step 6.
3. Otherwise acquire the install lock (named mutex). Waiters attach to the installer's progress, which is shared through a small state file or pipe.
4. Download the release manifest from the Pages site (`/runtime/<channel>/release.json`) over HTTPS.
5. Verify the manifest (section 8), download the runtime zip with resume, verify hashes, extract to `runtime\<ver>\`, write `current.txt`, then launch the broker.
6. Ask the broker for the model status. It then fetches any missing EP and model (resumable, verified), and reports progress.
7. Check licence acceptance and disk space before large downloads. Honour `AcquisitionPolicy` and metered-network settings.

Failure modes to handle and test: offline, proxy and captive portal, low disk, AV quarantine, killed mid-download, two apps racing, a partial extract.

## 8. Trust model (no code-signing certificate needed in v1)

- **Release manifest** (`release.json`) lists channel, version, `minVersion`, a monotonic `sequence`, an `expires` time, the files and their SHA-256 hashes, and a kill-switch list. It is signed with Ed25519.
- Key hierarchy:
  - A root key, offline, embedded in the client.
  - A signing key, certified by the root, with rotation and revocation lists in the manifest.
  - Separate keys for the runtime, the catalog and the models.
- Rules:
  - Refuse any manifest with a sequence lower than the one stored, or one that is expired (anti-rollback and anti-freeze).
  - Verify every file hash before execution.
  - HTTPS with pinning to the Pages host is a second layer. Pinning is optional, because a GitHub certificate rotation could break old clients, so the signed manifest stays the root of trust.
- Model files are verified by hash from the signed catalog before they are ever mapped.
- Host adapters are allow-listed and shipped in the runtime. No downloaded code is executed.
- Threat note: per-user installs can be tampered with by any malware running as that user. This is accepted in v1 and is not a boundary.

## 9. Self-update

- The updater runs in the broker. It checks on a timer (12-24 h with jitter) and on first use after a long idle.
- A per-user scheduled task (no admin) runs a short updater at logon and daily, so the runtime stays fresh with no app running.
- Flow:
  1. Fetch and verify the manifest.
  2. Download and verify the new version into `runtime\<new>\`.
  3. Start the new broker side by side and run a health check.
  4. New connections go to the new broker.
  5. The old broker drains: no new sessions, in-flight work finishes, then it exits.
  6. Flip `current.txt` and record `lastKnownGood`.
  7. Delete old versions after a grace period.
- Rollback: a failed health check or a crash loop (3 failures in 10 minutes) reverts to `lastKnownGood` and reports it.
- Controls: channels (stable, beta, pinned), staged rollout percent, a kill switch, and a registry or policy value to disable updates or set a mirror URL.

## 10. Catalog

- A signed `catalog.json` is updated independently of the runtime. Each entry holds:
  - id and task type;
  - version and files (url, size, SHA-256);
  - licence id and text URL;
  - minimum runtime version;
  - hardware requirements and the preferred EP order;
  - the adapter id and its parameters.
- Adding a model is a catalog change only. A new task type needs a new adapter and a new API.
- Model files are shared and deduplicated by hash. Garbage collection removes unreferenced versions, using LRU and a configurable cap.

### 10.1 Model files hosted on Hugging Face (v1)

For now the catalog points at `.onnx` and related files that already exist on Hugging Face. We publish no model files ourselves.

Catalog file entries:
- `url`: a **revision-pinned** Hugging Face URL of the form `https://huggingface.co/<owner>/<repo>/resolve/<commit-sha>/<path>`. Never `main`, because a moving branch would change the file and break the hash.
- `size` and `sha256` for every file.
- `hfRepo` and `hfRevision`, kept for tooling, the docs catalog browser and licence display.
- A model is usually **several files** (`.onnx`, external `.onnx_data`, tokenizer files, `genai_config.json` and so on). The entry lists all of them, and the store keeps them together under `models\<id>\<ver>\`.

Rules:
- **Integrity does not depend on Hugging Face.** The client verifies every file against the SHA-256 in our signed catalog before the host ever maps it. A changed or tampered file fails verification.
- **Host allow-list:** the client downloads only from hosts in a built-in allow-list (`huggingface.co` and the Hugging Face CDN hosts that its redirects resolve to). The allow-list is part of the signed release manifest so it can be updated. A catalog entry pointing elsewhere is rejected.
- **No gated or private models in v1.** A file must be downloadable anonymously, without a token or a licence-click gate. CI rejects entries that need authentication.
- **Licences:** the catalog records each model's licence id and a link, and `LicenseNotAccepted` applies as before. Review each model's licence for redistribution and use terms before adding it. We are linking to the files, not redistributing them, but the licence still applies to the user.
- **Mirror support:** a policy setting can rewrite the Hugging Face host prefix to an internal mirror. The hash check keeps this safe. It also covers enterprises that block Hugging Face.

Catalog tooling (`/tools`):
- `catalog add <hf-repo> <revision>` resolves the commit sha, lists the files and reads their SHA-256 values from the Hugging Face API (LFS files expose their hash), and writes the catalog entry.
- CI checks every changed entry: the URL resolves, the size matches, the hash matches the Hugging Face metadata, the repo is public and not gated, and the licence is recorded.
- A scheduled job re-checks that every catalog URL is still reachable, and opens an issue if a file or repo has been removed or moved.
- Full-file hash verification runs on demand and on a schedule, not on every PR, because the files are large.

Risks and mitigations:
- **Availability and rate limits:** Hugging Face may throttle, change its CDN behaviour, or remove a repo. The scheduled check catches removal early. The long-term fix is our own mirror behind the same catalog indirection (sprint 11+).
- **Redirects:** downloads redirect to CDN hosts and the redirect URLs expire. The downloader must resolve the redirect per resume, and it must not cache a redirect target.
- **Conversion:** many models on Hugging Face are not ready-to-run ONNX for onnxruntime-genai. Prefer publisher-provided ONNX repos. If we need our own conversion, that becomes a decision to host files ourselves.
- **Privacy:** downloads expose the user's IP to Hugging Face. Document this on the privacy page.

## 11. Signing readiness and known risks

- v1 ships unsigned `broker.exe` and `host.exe`. Microsoft-signed ONNX Runtime and Windows ML DLLs remain signed.
- Risks:
  - SmartScreen and Defender reputation. Files written by our own process carry no Mark-of-the-Web, and we must test on default Defender.
  - AppLocker and WDAC environments will block the binaries. Document this as unsupported until signing.
  - AV false positives on a self-updating process that opens a pipe.
- Signing plan: add an Authenticode step to the build (Azure Trusted Signing is the likely route). The verifier accepts it in addition to the manifest signature. Then add optional MSIX or per-machine mode.

## 12. Security requirements

- Pipe ACL is current user only. Each connection gets its own session and security context. Caches are never shared across apps.
- Hosts run with no network, with a restricted token or AppContainer, and with job limits.
- Prompts, audio and images are never logged or sent in telemetry.
- Input validation covers all size and format fields, and the host fuzzes its parsers (image, audio, tokenizer).
- Visible uninstall: an `uninstall` command that removes runtime, EPs and models, plus an optional Add/Remove Programs entry.

## 13. Telemetry and privacy

- Opt-out, minimal and content-free: install and update outcomes, failure codes, model and EP ids, latency buckets and crash counts.
- The privacy statement is documented, and the enterprise policy value disables it.

## 14. Testing

- Unit tests and golden tests per adapter.
- Protocol compatibility matrix (old clients against new brokers, and the reverse).
- Chaos tests: concurrent first-run installs, kill during download, extract or swap, a corrupted file, a manifest replay, disk full and an expired manifest.
- Security tests: pipe access from another user, path-traversal in zips, hash-mismatch and rollback attempts.
- Perf tests: first-run time, cold and warm load, tokens per second, and memory with two apps sharing one model.
- Size gates: `Core` at most 1 MB and each task package at most 500 KB.
- Package independence: a test app that references a new `Chat` with an older `Ocr` must restore, build and run against the same runtime. The matrix includes mixed task-package versions.
- Matrix: Windows 10 22H2, 11 and 11 ARM64, with CPU-only, GPU and NPU devices.

## 15. Repository and hosting (GitHub)

One monorepo holds the client, broker, host, adapters, catalog, docs and build tooling.

```
/src/Core              CommunityToolkit.AI.Core (transport, bootstrapper)
/src/Chat              CommunityToolkit.AI.Chat (client package)
/src/SpeechToText      CommunityToolkit.AI.SpeechToText (client package)
/src/Ocr               CommunityToolkit.AI.Ocr (client package)
/src/Broker            broker.exe
/src/Host              host.exe + task adapters (chat, speech, OCR)
/src/Protocol          core protocol + per-task contract schemas + compat tests
/catalog               catalog.json sources, per-model metadata, licences
/docs                  end-user docs (static site source)
/samples               packaged and unpackaged sample apps
/tools                 site builder, feed generator, manifest signer
/.github/workflows     CI, PR staging, release, scheduled jobs
```

### 15.1 What GitHub Pages hosts

The published site is laid out as follows. The URLs are relative to the site root, which is the production root or a PR staging root.

| Path | Content |
|---|---|
| `/` | End-user docs (section 17) |
| `/nuget/v3/index.json` | Static NuGet v3 feed (section 16) |
| `/runtime/<channel>/release.json` | Signed release manifest |
| `/runtime/<ver>/runtime.zip` | Runtime payload (small, tens of MB) |
| `/catalog/catalog.json` | Signed model catalog |

### 15.2 Pages limits and where model files live

GitHub Pages is a fit for the manifests, the runtime zip, the docs and the feed. It is a poor fit for multi-GB model files:
- A published site is limited to about 1 GB, and a git file is limited to 100 MB.
- Pages has a soft bandwidth limit of about 100 GB per month.
- It is not intended as a general file CDN.

Decision for the prototype:
- **Pages is the stable URL surface** for everything the client fetches first (manifests, catalog, runtime).
- **Model files are not hosted by us.** The signed catalog points directly at files on Hugging Face (section 10.1). We host only the small catalog entries.
- The signed catalog stores each file URL, size and SHA-256. The client never hard-codes file locations.
- Because of that indirection, we can move files to our own CDN later (Azure Front Door or similar) with no client change, only a catalog change.
- Execution providers are acquired through Windows ML's own mechanism where it provides one. How EP packages are distributed is a separate decision, tracked in the open questions.
- Verify early, in sprint 0, that resumable downloads work: range requests against Pages, and against Hugging Face file URLs including their redirects.
- The repository must be public for free Pages hosting. Private repositories need a plan that supports it, and a private site breaks anonymous client downloads.

### 15.3 Environments and keys

| Environment | URL root | Signing |
|---|---|---|
| Production | `https://andrewleader.github.io/CommunityToolkit.AI/` | Release key, held in a protected GitHub Environment with required reviewers |
| PR staging | `https://andrewleader.github.io/CommunityToolkit.AI/pr/<number>/` | Ephemeral staging key, generated per CI run or per repo, trusted only in dev mode |
| Local dev | `file://` or localhost | Dev key, in dev mode only |

- The production root public key is baked into the client. A **dev mode** (an explicit opt-in env var such as `LOCALAI_DEV_ROOT_URL` plus `LOCALAI_DEV_TRUST_KEY`) lets a developer point the client at a staging root and trust its staging key.
- Dev mode is disabled in release builds of the client unless the app opts in. This stops an attacker from redirecting the client to an arbitrary host through an environment variable.
- The production signing key is never available to PR workflows. Fork PRs get no secrets at all.

## 16. NuGet feed on GitHub Pages

- The feed is a **static NuGet v3 feed** generated by `/tools` at build time: `index.json`, `flatcontainer/<id>/index.json` and the `.nupkg` files, plus registration pages.
- Packages on the feed: `CommunityToolkit.AI.Core`, `CommunityToolkit.AI.Chat`, `CommunityToolkit.AI.SpeechToText` and `CommunityToolkit.AI.Ocr`. Each has its own version history.
- A developer adds it with:
  `dotnet nuget add source https://andrewleader.github.io/CommunityToolkit.AI/nuget/v3/index.json -n CommunityToolkit-AI-Preview`
- Packages are **prerelease versions** (for example `Chat` `0.1.0-preview.<run>`) until we publish to nuget.org. Developers must opt in to prerelease versions.
- **Independent versioning per package:**
  - Each package has its own version, computed per package (for example with Nerdbank.GitVersioning or MinVer and a per-package tag prefix such as `chat-v`, `ocr-v`, `core-v`).
  - A PR that only touches `/src/Ocr` bumps only `Ocr`. CI uses path filters, plus dependency detection, so a change to `Core` or to a shared contract rebuilds and re-versions the dependent packages.
  - Because of this, an old version of an unchanged package stays valid and is never forced forward.
- The service index uses absolute URLs, so the generator takes the **site base URL as an input**. That is how each PR staging site gets a working, self-consistent feed.
- Static feeds have no push API, no search and no delete. The generator rebuilds the full feed from the packages kept in the repo's release assets or the previous site, and it preserves **all** old versions of every package, so that apps pinned to an older `Ocr` still restore.
- Packages are not signed in v1. NuGet package signing is deferred, with the same signing-readiness plan as the binaries.
- Risks to verify in sprint 0: the NuGet client accepting the static layout and content types from Pages, and the cache headers (Pages caches for about 10 minutes, so a new version may take a few minutes to become visible).

## 17. End-user documentation

- Docs are written in Markdown under `/docs` and built into a static site (DocFX, Docusaurus or MkDocs, chosen in sprint 0). They are published at the Pages root.
- Content:
  - Quick start (unpackaged console app, packaged WinUI app).
  - Per-task guides (chat, OCR, speech) with copy-pasteable samples.
  - Model catalog browser, generated from `catalog.json`.
  - How `EnsureReadyAsync` works: what is downloaded, where it goes, how big it is, how to show progress.
  - Using the preview NuGet feed.
  - Enterprise settings: update controls, mirrors, disabling telemetry.
  - Troubleshooting (AV, proxies, disk space) and error code reference.
  - Uninstall and cleanup.
  - Privacy and telemetry statement.
- API reference is generated from the XML doc comments of each package on every build, and it is versioned per package (for example `Chat` 1.x and 2.x docs both stay available).
- Each task guide has a version selector and a migration guide for every major version of that task.
- Docs samples are **compiled in CI** from the `/samples` projects, so docs never show code that doesn't build.
- A link checker and a spell check run in CI.

## 18. CI/CD and PR staging

### 18.1 Pull request workflow (every PR)

1. **Build and test:** build all projects for x64 and ARM64, run unit tests, the protocol compatibility matrix and the per-package size gates (`Core` at most 1 MB, each task package at most 500 KB). Path filters decide which packages are affected.
2. **Stage the full site** into `pr/<number>/`:
   - the docs site, built with the PR's base URL;
   - the NuGet feed, with the PR's changed `.nupkg` files (each version `x.y.z-pr<number>.<run>`, only for affected packages) and absolute URLs rewritten to the PR root. Unchanged packages resolve from the production feed;
   - the runtime zip and a `release.json` signed with the staging key, channel `pr-<number>`;
   - a `catalog.json` that overlays the production catalog with the PR's new or changed models.
3. **Model files for PRs:** nothing is uploaded. A PR that adds or changes a model only changes the catalog entry, which points at Hugging Face. The staging `catalog.json` overlays the production catalog with the PR's entries, and CI runs the catalog checks from section 10.1.
4. **Deploy** the staging site to Pages. Pages has no native per-PR preview, so use a `gh-pages`-style branch with a `pr/<number>/` folder. A preview action (or a small custom workflow) writes and removes that folder.
5. **End-to-end smoke test** against the staging URLs, on a clean Windows runner: install the PR's NuGet package from the staging feed, run a sample app in dev mode, and call `EnsureReadyAsync()`. The test checks that the runtime, a tiny test model and a response all work.
6. **Comment on the PR** with links: docs, NuGet source command and version, runtime manifest URL, catalog URL, and the dev-mode env vars to try it.
7. **Cleanup:** when the PR closes, delete the `pr/<number>/` folder.

Constraints:
- PRs from forks cannot write to Pages. They get build, test and an uploaded artifact only. A maintainer can re-run staging by pushing the branch or labelling the PR.
- The total Pages site, with all open PR folders, stays well under 1 GB. This is another reason PR staging holds only small files.
- A cap on open staged PRs, plus cleanup of stale folders, is enforced by a scheduled job.

### 18.2 Main branch (continuous preview)

- Every merge to `main` builds and publishes the `preview` channel to the Pages root: docs, feed (`-preview.<run>` versions), runtime manifest and catalog. This is the always-fresh build developers consume.
- A scheduled job (daily) checks that the live site is consistent: the manifests verify, the hashes match, and the links work.

### 18.3 Release (promotion)

- A `stable` channel is promoted by a manual workflow in a protected GitHub Environment with reviewers. It re-signs the manifest with the production key and flips `/runtime/stable/release.json`. Promotion is a manifest change, so it is instant and reversible.
- Staged rollout percent and the kill switch are fields in the manifest (section 9), so rollback is another manifest change.
- Release notes are generated from merged PRs and published on the docs site.

## 19. Agile delivery plan

Principles:
- Two-week sprints. Every sprint ends with something a developer can install from the preview feed and try.
- Thin vertical slices. Build the whole path from NuGet package to a response first, then widen it.
- The pipeline comes first. The PR staging pipeline is built in sprint 1, so every later change is previewable.
- Contracts are frozen early, deliberately. The `Core` package, the core protocol and the manifest format get the most review, because every task package depends on them. Task contracts start unstable and are frozen per task when that task reaches 1.0.
- Each sprint has a demo, and the backlog is reprioritised from what we learn.

| Sprint | Slice (demo at the end) | Notes |
|---|---|---|
| 0 (1 wk) | Feasibility: a host exe running one small LLM through Windows ML and genai. Pages and Hugging Face range requests and redirects, NuGet static feed and Windows ML redistribution checked. Pick the first model and confirm that its ONNX files are anonymous, ungated and in a ready-to-run layout | Gate: decide go or no-go on Pages, Hugging Face hosting and the redistribution terms. Pick the docs tool. |
| 1 | The pipeline: repo skeleton with the `Core` and `Chat` packages as stubs, build, tests, per-package versioning and path filters, PR staging to `pr/<n>/`, docs shell, a static NuGet feed with placeholder packages, the PR comment bot | Gate: a PR produces a working docs site and feed URL, and a change to one package bumps only that package. |
| 2 | Hello world: `CommunityToolkit.AI.Core` and `.Chat` talk to a broker, which spawns a host, which streams chat from a bundled tiny model. Installed from the PR feed | The first end-to-end preview. Core protocol and Chat contract v0 (unstable). |
| 3 | Bootstrapper and cold start: signed manifest, runtime zip download from Pages, the install lock, and `EnsureReadyAsync` on a clean machine | Staging key and dev mode work. |
| 4 | Real model acquisition: the catalog and its Hugging Face tooling (section 10.1), multi-file, resumable and verified downloads, progress, disk checks, and `qwen-2.5-1.5b-instruct` working | Public preview announcement to the team. |
| 5 | Sharing and isolation: two apps share one host, sessions are isolated, idle eviction, crash recovery | Freeze protocol v1 at the end. |
| 6 | Self-update: side-by-side, drain, rollback, the scheduled task, the `stable` promotion workflow | Chaos tests added to CI. |
| 7 | `CommunityToolkit.AI.Ocr`: the package, the adapter and the shared-memory data plane. Proves that a new task package ships without touching `Chat` | Docs guide and sample. |
| 8 | `CommunityToolkit.AI.SpeechToText` (Whisper) | Docs guide and sample. |
| 9 | EPs and devices: GPU and NPU selection, fallback, ARM64 | Hardware matrix. |
| 10 | Hardening: the sandbox, fuzzing, telemetry, uninstall, enterprise policy, and a security review | Beta label proposed. |
| 11+ | Signing (Authenticode, Trusted Signing), our own mirror or CDN for model files (a catalog-only change), publishing to nuget.org, optional per-machine and MSIX mode | Driven by feedback and by the risks in section 11. |

Working agreements:
- Trunk-based development with short-lived branches. Every PR has a staging preview and links to the docs it changes.
- Definition of done: tests pass, docs and a sample updated, the size gate passes, the staging smoke test passes, and the protocol compatibility matrix is green.
- Feature flags in the catalog and manifest let incomplete tasks ship dark.
- A weekly dogfooding group of internal apps consumes the `preview` feed, and their feedback feeds the backlog.
- Metrics tracked per sprint: time to first response on a clean machine, package sizes against their budgets, install success rate, and update success rate.

## 20. Decisions and open questions

Decided:
- The repository is `andrewleader/CommunityToolkit.AI` and is public. The Pages root is `https://andrewleader.github.io/CommunityToolkit.AI/`. If the repo later moves to an org (for example the CommunityToolkit org), the feed and manifest URLs change, so the client's default root URL must be updated through a manifest redirect or a client release.
- The deprecation window for an old task contract major version is 12 months.
- Only `Core` types are shared across packages (`Model`, status, progress, errors and options). All input and result types belong to their task package.
- Model files are not hosted by us in v1. The catalog points at revision-pinned Hugging Face files, verified by hash.
- The bootstrapper doesn't show any UI, it's entirely app-driven through progress events.
- Non-.NET projections (C++, Rust, JS) in a future version
- Execution-provider packages are acquired through Windows ML `ExecutionProviderCatalog` APIs.
- Accept a dependence on Hugging Face availability for the preview.
