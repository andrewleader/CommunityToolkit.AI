# Enterprise settings

Policy values live under `HKLM` or `HKCU` `\Software\Policies\CommunityToolkit\LocalAI`. Machine values win.

| Value | Type | Effect |
|---|---|---|
| `DisableUpdates` | DWORD 1 | The runtime never updates itself. |
| `Channel` | String | Pins the update channel, for example `stable`. |
| `RootUrl` | String | Replaces the site root (manifests, catalog, runtime) with an internal mirror of the GitHub Pages site. |
| `HuggingFaceMirror` | String | Rewrites `https://huggingface.co/` in model URLs to an internal mirror, for networks that block Hugging Face. |
| `DisableTelemetry` | DWORD 1 | Turns off telemetry. |

Mirrors are safe: the client still verifies every manifest signature and every file hash against the signed catalog, so a
mirror can only serve the exact bytes we published.

## Updates

The runtime checks for updates every 12-24 hours (with jitter) and on first use after a long idle. A daily per-user
scheduled task (`CommunityToolkit.AI\LocalAI Updater`, no admin) keeps it fresh even when no app runs. A new version is
installed side by side, health-checked, then switched to; the old broker drains and exits. If the new version fails to
start three times in ten minutes, the runtime rolls back to the last known good version.

Releases use staged rollout percentages and a kill switch in the signed manifest.

## AppLocker and WDAC

The v1 binaries are not Authenticode-signed, so AppLocker and WDAC policies that require signed code block them. This is
unsupported until signing ships (see the roadmap).
