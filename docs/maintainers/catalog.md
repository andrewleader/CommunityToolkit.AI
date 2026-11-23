# Adding a model

Model files stay on Hugging Face. A catalog entry pins a commit and records every file's size and SHA-256, and the client
verifies every byte against our signed catalog before the host maps it.

## 1. Pick a repo

- Prefer publisher-provided ONNX repos in the ONNX Runtime GenAI layout (`genai_config.json` next to the model).
- The repo must be public and not gated: files must download anonymously.
- Review the licence for redistribution and use terms. We link to the files rather than redistributing them, but the
  licence still applies to the user.

## 2. Generate the entry

```powershell
dotnet run --project tools/LocalAI.Tools -- catalog add <owner>/<repo> <revision> `
  --include <subfolder>/ --id <catalog-id> --task chat --adapter ort-genai-chat `
  --license-id <SPDX id> --license-url <url> --display-name "<name>" --min-memory-mb 4096 --eps cpu `
  --param maxLength=4096
```

`catalog add` resolves the revision to a commit sha, lists the files, takes the SHA-256 of LFS files from the Hugging Face
API (and downloads and hashes small non-LFS files), and writes `catalog/models/<id>.json` with revision-pinned URLs
(never `main`).

Use `--hidden` to ship an entry dark: it then resolves only for dev-mode clients.

Adapter parameters for `ort-genai-chat`: `maxLength` (context length per session, default 4096),
`maxCachedSessions` (sessions that keep a KV cache, default 4), `chatTemplate` (overrides the model's template),
`subfolder`.

## 3. Open a PR

CI checks every changed entry: the URLs are revision-pinned, the repo is public and ungated, sizes and hashes match the
Hugging Face metadata, the files download anonymously, and the licence is recorded. The PR's staging catalog overlays
production with your entry, so you can try it right away from the staging site.

A scheduled job re-checks every URL daily (and every hash weekly) and opens an issue if a file or repo moves.

## Current models

`qwen-2.5-1.5b-instruct` uses `khanuckaeff/Qwen2.5-1.5B-Instruct-Olive-Onnx` (`cpu_and_mobile`), an int4 community
conversion of `Qwen/Qwen2.5-1.5B-Instruct` (Apache-2.0). Qwen does not publish a GenAI-format ONNX build, and the
`onnx-community` builds target Transformers.js rather than GenAI. Replacing it with a publisher-provided build (or our own
conversion, which would mean hosting files ourselves) is an open decision.

`test-echo` is the hidden smoke-test model. Its single tiny file exists only so the whole acquisition path runs; the
`test-echo` adapter replies with an echo.
