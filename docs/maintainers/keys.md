# Keys and signing

## Hierarchy

| Key | Where it lives | Used for |
|---|---|---|
| Root | Offline (never on a CI machine). Its public key is embedded in every client and runtime (`src/Protocol/Trust/TrustRoots.cs`). | Certifying signing keys. |
| Runtime signing key | GitHub Environment `preview-signing` (`RUNTIME_SIGNING_KEY`, `RUNTIME_SIGNING_CERT`) | `runtime/preview/release.json` |
| Release key | GitHub Environment `release`, with required reviewers (`RELEASE_SIGNING_KEY`, `RELEASE_SIGNING_CERT`) | `runtime/stable/release.json` |
| Catalog signing key | GitHub Environment `preview-signing` (`CATALOG_SIGNING_KEY`, `CATALOG_SIGNING_CERT`) | `catalog/catalog.json` |
| Models key | Reserved for signing model metadata when we host files ourselves. | — |
| Staging key | Generated per PR run, trusted only in dev mode | PR staging sites |

A certificate binds a signing key to the root for a set of purposes and an expiry. A runtime key cannot sign a catalog,
and the reverse. PR workflows never see the production keys, and fork PRs get no secrets at all.

## Creating the production keys

```powershell
dotnet run --project tools/LocalAI.Tools -- keygen --out <offline-folder> --valid-days 730
```

This writes `root.key` / `root.pub` and a key plus certificate per purpose. Then:

1. Put `root.pub` into `TrustRoots.Production`.
2. Store each `*.key` file's contents and each `*.cert.json` file's contents as the environment secrets above.
3. Certify a separate release key for the stable channel:

    ```powershell
    dotnet run --project tools/LocalAI.Tools -- certify --root-key <offline-folder>/root.key `
      --public-key <release public key> --purposes runtime --valid-days 365 --out release.cert.json
    ```

4. Move `root.key` offline (for example to a hardware-backed vault), and delete every other copy.

## Rotation and revocation

- **Rotate a signing key:** generate a new key, certify it with the root, update the secrets. Old manifests stay valid until
  they expire.
- **Revoke a key:** publish a manifest with `--revoked-keys <keyId>`. Clients stop trusting anything signed by that key,
  including catalogs.
- **Rotate the root:** add the new root's public key to `TrustRoots.Production` next to the old one, ship clients and
  runtimes, then start certifying with the new root and revoke the old root's id.

## Anti-rollback and anti-freeze

Every manifest and catalog has a monotonic `sequence` and an `expires` time. Clients remember the highest sequence they
have seen per document and refuse anything older or expired. The scheduled workflow re-signs the preview manifest and
the catalog daily so they never expire; stable manifests last 90 days and the scheduled check opens an issue when one
expires within 7 days.
