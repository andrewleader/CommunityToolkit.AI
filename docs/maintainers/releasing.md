# CI, staging and releases

| Workflow | Trigger | What it does |
|---|---|---|
| `ci.yml` | called by the others | Build, protocol and end-to-end tests on x64 and ARM64, pack, size gates, self-contained runtime zips, catalog checks, docs build, spell and link checks. |
| `pr.yml` | every PR | CI, then for same-repo PRs: stage `pr/<n>/` (docs, feed with affected packages, runtime manifest on channel `pr-<n>` signed with an ephemeral key, catalog overlay), a smoke test on a clean Windows runner, and a PR comment with links. |
| `pr-cleanup.yml` | PR closed | Removes `pr/<n>/`. |
| `preview.yml` | push to `main` | CI, then publishes the `preview` channel to the site root: docs, feed (all old versions kept), runtime, catalog. |
| `release.yml` | manual, `release` environment with reviewers | Promotes the preview manifest to `stable` (re-signed with the release key), with rollout percent, kill switch and min version. Creates a GitHub Release with generated notes. |
| `scheduled.yml` | daily | Verifies the live site, checks every catalog URL (full hashes weekly), re-signs preview manifests and the catalog, removes stale PR staging folders. |

## Versioning

Each package is versioned independently with MinVer and a per-package tag prefix: `core-v`, `chat-v`, `ocr-v`,
`stt-v`. Tag `chat-v1.0.0` to release Chat 1.0.0. Between tags, versions are prereleases: `-preview.<run>` from `main`
and `-pr<n>.<run>` from PRs. `localai-tools affected` maps changed paths to packages: a change to `src/Core`, the core
protocol, trust code or build files affects every package; a task folder affects only that package. Only affected
packages are published to a PR's feed.

Task packages depend on Core with the range `[x.y.z, 2.0.0)`.

## Pages

The site is the `gh-pages` branch, rewritten as one orphan commit on every deploy so binaries never pile up in history.
Deploys share one concurrency group. The deploy action prunes runtime versions that no channel references (keeping the
newest two) and fails if the site would exceed 950 MB.

Smoke tests read the staged site through `file://` (the same bytes that were deployed), because Pages caches for about
10 minutes.

## Rolling back

Promotion is a manifest change, so rollback is too: run `release.yml` with `source` set to an older manifest, or set
`kill-switch` to the bad version (clients on it update to the manifest's version). A runtime that crash-loops on a
machine rolls back to the last known good version on its own.
