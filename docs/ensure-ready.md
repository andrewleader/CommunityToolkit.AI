# How EnsureReadyAsync works

`EnsureReadyAsync` is idempotent: call it whenever you are about to use a model. When everything is in place it returns
in milliseconds.

## What it does

1. **Connect to the broker.** The broker is one process per user that owns downloads, the model store and the model
   hosts. If it is running, skip to step 4.
2. **Start the installed runtime.** If `runtime\current.txt` points at a runtime whose files verify, start its
   `broker.exe` and wait for its pipe.
3. **Install the runtime.** Otherwise take the install lock (other apps installing at the same time wait and share the
   progress), download the signed release manifest from
   `https://andrewleader.github.io/CommunityToolkit.AI/runtime/<channel>/release.json`, verify its signature, sequence
   and expiry, download the runtime zip for this CPU (resumable), verify its hash, extract it safely, verify every file's
   hash, then point `current.txt` at it.
4. **Check the model.** The broker resolves the id in the signed catalog, checks the task matches, checks hardware, and
   reports the licence and the bytes to download.
5. **Ask the app.** If the licence needs acceptance, your `LicenseAcceptance` callback runs. Under
   `AcquisitionPolicy.Prompt`, your `DownloadConsent` callback runs. `AcquisitionPolicy.Never` refuses to download.
6. **Download the model.** Every file is downloaded from its revision-pinned Hugging Face URL with resume, verified
   against the SHA-256 in the signed catalog, stored once by hash, and assembled into the model folder, which is renamed
   into place only after everything verifies. Several apps asking at once share one download.

## Progress

`EnsureProgress` has a `Stage` (`Runtime`, `Ep`, `Model`, `Verify`), the bytes done and total for that stage, and an
`OverallFraction` from 0 to 1 across all stages, so one progress bar is enough.

## Where things go

```
%LocalAppData%\CommunityToolkit\LocalAI\
  runtime\<version>\      broker.exe, host.exe and their DLLs, files.json (every hash)
  runtime\current.txt     the active version
  models\<id>\<version>\  model files + model.json
  models\.blobs\          files stored once by SHA-256
  staging\                partial downloads (resumed next time)
  state\                  config, licences, update state, last-known-good
  logs\
```

## Sizes

| Piece | Size |
|---|---|
| Runtime (per architecture) | about 50-60 MB download |
| `qwen-2.5-1.5b-instruct` | about 1 GB |

Disk space is checked before each download, with a safety margin; a shortfall raises `InsufficientDiskException`.
