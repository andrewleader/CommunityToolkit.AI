using CommunityToolkit.AI.Tools;

// localai-tools: build and release tooling (spec section 15). Run with no arguments for help.
if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        localai-tools <command> [options]

        Keys and signing
          keygen --out <dir> [--valid-days 365]               Root key + runtime/catalog/models signing keys and certificates
          certify --root-key <k> --public-key <b64> --purposes a,b --out <cert.json> [--valid-days 365]
          sign --purpose runtime|catalog --in <payload.json> --key <k> --cert <c> --out <signed.json>

        Runtime
          pack-runtime --broker <dir> --host <dir> --version <v> --out <runtime.zip> [--rid win-arm64]
          release-manifest --channel <c> --version <v> --runtime-zip <zip> [--runtime-zip <zip2>] --zip-base-url ../<v>/ --out <payload.json>
                           [--sequence n] [--expires-days 30] [--min-version v] [--rollout 100] [--kill-switch a,b]
                           [--revoked-keys a,b] [--deprecated chat/0] [--catalog-url ../../catalog/catalog.json]
                           [--download-hosts h1,h2] [--redirect-root-url url]
          promote --from <signed release.json|url> --channel stable --out <payload.json> [--rollout n] [--kill-switch a,b] [--min-version v]
          verify-site --root <url> --channel <c> [--trust-key b64] [--download]

        Catalog (models stay on Hugging Face, pinned by commit)
          catalog add <hf-repo> <revision> --id <id> --task chat --adapter ort-genai-chat --license-id <spdx>
                      [--license-url url] [--include <folder/>] [--display-name n] [--min-memory-mb n] [--eps cpu]
                      [--param k=v ...] [--version v] [--hidden] [--out catalog/models/<id>.json]
          catalog build --models <dir> --out <payload.json> [--sequence n] [--expires-days 30] [--overlay <signed catalog file|url>]
          catalog check --models <dir> [--reachability] [--full-hash] [--changed a.json,b.json]
          catalog docs --models <dir> --out <catalog.md>

        NuGet feed (static v3 on GitHub Pages)
          feed build --packages <dir> --base-url <url> --out <dir> [--previous-base-url <url>]
          size-gate --packages <dir>
          affected --base <git-ref>
        """);
    return 0;
}

try
{
    var a = new Args(args.Skip(1).ToArray());
    return args[0] switch
    {
        "keygen" => KeyCommands.Keygen(a),
        "certify" => KeyCommands.Certify(a),
        "sign" => KeyCommands.Sign(a),
        "pack-runtime" => RuntimeCommands.PackRuntime(a),
        "release-manifest" => RuntimeCommands.ReleaseManifest(a),
        "promote" => await RuntimeCommands.PromoteAsync(a),
        "verify-site" => await RuntimeCommands.VerifySiteAsync(a),
        "catalog" => await CatalogCommands.RunAsync(a),
        "feed" => await FeedCommands.RunAsync(a),
        "size-gate" => FeedCommands.SizeGate(a),
        "affected" => FeedCommands.Affected(a),
        _ => throw new ToolException($"Unknown command '{args[0]}'."),
    };
}
catch (ToolException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
