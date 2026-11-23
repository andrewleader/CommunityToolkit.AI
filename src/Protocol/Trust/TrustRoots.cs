namespace CommunityToolkit.AI.Protocol.Trust;

/// <summary>
/// Root public keys baked into every client and runtime build. The matching private keys are kept offline
/// and only ever used to certify signing keys (see docs/maintainers/keys.md).
/// </summary>
internal static class TrustRoots
{
    public static readonly string[] Production =
    [
        "bFDIj1Nm07A2G+N+NI2QyJdk4MKyoCUP2SutHNnWJDc=",
    ];
}
