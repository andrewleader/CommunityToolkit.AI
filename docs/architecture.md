# Architecture

```
App process                     Broker (1 per user)               Model hosts (N)
+---------------------+  pipe   +--------------------------+ spawn +----------------------+
| Client library      |<------->| Catalog, store, updater, |<----->| host.exe             |
| API + RPC stub      |         | EP manager, host manager |       | ONNX Runtime GenAI   |
| Bootstrapper        |         +--------------------------+       | task adapter         |
+---------------------+                                            +----------------------+
```

- **Client libraries** (`src/Core`, `src/Chat`, ...) contain the API, the bootstrapper and the pipe transport. No model
  code. Core links the core protocol and trust sources from `src/Protocol`, so it has no package dependencies.
- **Broker** (`src/Broker`, `broker.exe`): one per user, singleton by lock file, serving
  `\\.\pipe\LocalAI-<userSid>-v1` with an ACL for the current user only. It resolves the signed catalog, downloads and
  verifies models, starts hosts, records licences, updates the runtime and idles out after about 10 minutes without
  clients.
- **Hosts** (`src/Host`, `host.exe`): one per (model, version, EP, runtime version), shared by every app. Each runs in a
  kill-on-close job object with memory (and optional CPU) limits, serves sessions over its own current-user-only pipe,
  schedules requests through a per-model fair queue that favours the foreground app, and exits after 5 idle minutes.

## Wire protocol

Frames are a 4-byte little-endian length plus a UTF-8 JSON envelope (`id`, kind, method, payload, error, credits).
Fields are additive only and unknown fields are ignored. Streaming results are event frames with credit-based
backpressure: the caller grants credits as it consumes, and a slow app pauses generation instead of buffering.

The protocol is layered: the **core protocol** (handshake, status, acquisition, sessions, errors) and one **task
contract** per task (`chat` 1.0, `ocr` 0.1, `speech-to-text` 0.1), each versioned `major.minor`. Within a major the
lower minor wins. The runtime serves several majors of a task side by side; a client newer than the runtime gets
`ProtocolTooOld` (and triggers an update), a client older than every served major gets `NotSupported`.

## Trust

- The release manifest and the catalog are signed with Ed25519. Signing keys are certified by an offline root key that is
  embedded in the client, separately per purpose (runtime, catalog, models), with revocation in the manifest.
- Clients refuse a manifest with a lower sequence than the one they saw (anti-rollback) or an expired one (anti-freeze).
- Every runtime file and every model file is hash-verified before it is executed or mapped. Host adapters ship inside the
  runtime and are allow-listed; no downloaded code runs.
- Model downloads are only allowed from hosts in the signed manifest's allow-list, checked on every redirect hop.
- Per-user installs can be tampered with by malware running as the same user. This is accepted in v1.

## Not yet implemented

These parts of the plan are scheduled for later sprints: Windows ML execution providers (GPU/NPU) and the
`ExecutionProviderCatalog`, the restricted-token / AppContainer host sandbox, the shared-memory data plane for images and
audio, the OCR and speech adapters, Authenticode signing, and the C ABI for non-.NET apps.
