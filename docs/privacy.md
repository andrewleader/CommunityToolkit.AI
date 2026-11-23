# Privacy and telemetry

**Your content never leaves the device.** Prompts, responses, audio and images are processed only by the local model
host. They are never logged and never sent anywhere.

**Downloads.** The runtime downloads manifests, the catalog and runtime packages from
`andrewleader.github.io` (GitHub Pages), and model files from Hugging Face (`huggingface.co` and its CDN hosts). Like any
download, these requests reveal your IP address to GitHub and Hugging Face. Use the enterprise mirror settings to avoid
that.

**Telemetry** is opt-out, minimal and content-free. It records:

- install and update outcomes and failure codes,
- model ids and execution provider ids,
- latency buckets (for example "<2s", never exact timings),
- host crash counts.

In v1 telemetry is only written locally to `logs\telemetry-*.jsonl`; there is no upload endpoint yet. Turn it off with the
`DisableTelemetry` policy or by setting the environment variable `LOCALAI_TELEMETRY_OPTOUT=1`.
