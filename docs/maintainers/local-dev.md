# Local development

## Build and test

```powershell
dotnet build CommunityToolkit.AI.slnx
dotnet test tests/CommunityToolkit.AI.Protocol.Tests   # framing, RPC, Ed25519 (RFC 8032), manifests, zip safety, downloads
dotnet test tests/CommunityToolkit.AI.Runtime.Tests    # end to end: packages -> bootstrapper -> broker -> host
```

The end-to-end tests pack this repo's broker and host into a runtime zip, sign a local site with throwaway keys, serve it
on `127.0.0.1`, and drive the public API in dev mode. Each test uses its own install home under `%TEMP%`, so they never
touch your real install. They cover first run, licence and consent, sharing one host, crash recovery, cancellation,
integrity failures, concurrent installs and a side-by-side self-update.

## Run the sample against a local site

`tools/dev-site.ps1` builds a complete signed site with dev keys, using the same `localai-tools` commands CI uses:

```powershell
./tools/dev-site.ps1            # prints the four environment variables to set
$env:LOCALAI_DEV_ROOT_URL  = '...'
$env:LOCALAI_DEV_TRUST_KEY = '...'
$env:LOCALAI_CHANNEL       = 'local'
$env:LOCALAI_HOME          = '...'
dotnet run --project samples/ConsoleChat -- test-echo --dev --yes --prompt "hello"
dotnet run --project samples/ConsoleChat -- qwen-2.5-1.5b-instruct --dev --yes
```

The site is read through `file://` by default; add `-Serve` to serve it on `http://127.0.0.1:8787/` instead.

## Dev mode

Dev mode lets a client trust a non-production root URL and key. It is enabled when the app sets
`LocalAIRuntime.EnableDevMode = true` (it is on by default only in Debug builds of Core). Environment variables alone
cannot enable it in a release build, so they cannot redirect a shipping app.

| Variable | Meaning |
|---|---|
| `LOCALAI_DEV_ROOT_URL` | Site root (https, `http://127.0.0.1`, or `file://`). |
| `LOCALAI_DEV_TRUST_KEY` | Extra root public keys (base64, comma-separated). |
| `LOCALAI_CHANNEL` | Channel, for example `pr-42`. |
| `LOCALAI_HOME` | Install home. Kernel object names get a suffix so this never collides with the real install. |

Runtime tuning knobs, read by the broker and hosts: `LOCALAI_BROKER_IDLE_SECONDS`, `LOCALAI_HOST_IDLE_SECONDS`,
`LOCALAI_HOST_MEMORY_MB`, `LOCALAI_HOST_CPU_PERCENT`, `LOCALAI_UPDATE_INITIAL_DELAY_SECONDS`,
`LOCALAI_UPDATE_INTERVAL_SECONDS`, `LOCALAI_LOG_CONSOLE=1`, `LOCALAI_TELEMETRY_OPTOUT=1`.

## Repository layout

```
src/Core           CommunityToolkit.AI.Core (transport, bootstrapper, Model base)
src/Chat           CommunityToolkit.AI.Chat
src/SpeechToText   CommunityToolkit.AI.SpeechToText
src/Ocr            CommunityToolkit.AI.Ocr
src/Protocol       core protocol, task contracts, trust (linked into the client packages)
src/Broker         broker.exe
src/Host           host.exe and task adapters
catalog/models     one JSON entry per model
docs               this site (MkDocs)
samples            sample apps (compiled in CI)
tools              localai-tools, dev-site.ps1
tests              protocol and end-to-end tests
```
