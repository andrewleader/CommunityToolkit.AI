using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.AI.Protocol;
using CommunityToolkit.AI.Protocol.Trust;

namespace CommunityToolkit.AI.Protocol.Tests;

public class TrustTests
{
    // RFC 8032 section 7.1 test vectors 1-3.
    [Theory]
    [InlineData("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60", "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
    [InlineData("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb", "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
    [InlineData("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7", "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025", "af82",
        "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a")]
    public void Ed25519_MatchesRfc8032Vectors(string seedHex, string publicHex, string messageHex, string signatureHex)
    {
        byte[] seed = Convert.FromHexString(seedHex);
        byte[] message = Convert.FromHexString(messageHex);
        byte[] signature = Convert.FromHexString(signatureHex);

        Assert.Equal(publicHex, Convert.ToHexString(Ed25519.GetPublicKey(seed)).ToLowerInvariant());
        Assert.Equal(signatureHex, Convert.ToHexString(Ed25519.Sign(seed, message)).ToLowerInvariant());
        Assert.True(Ed25519.Verify(Convert.FromHexString(publicHex), message, signature));

        signature[5] ^= 1;
        Assert.False(Ed25519.Verify(Convert.FromHexString(publicHex), message, signature));
    }

    [Fact]
    public void SignedDocument_VerifiesOnlyForTrustedRootPurposeAndUntamperedPayload()
    {
        var keys = TestKeys.Create();
        byte[] payload = Encoding.UTF8.GetBytes("{\"hello\":1}");
        var document = DocumentSigner.Sign(payload, TrustPurposes.Runtime, keys.RuntimeSeed, keys.RuntimeCert);
        var store = new TrustStore([keys.RootPublic]);

        Assert.Equal(payload, store.Verify(document, TrustPurposes.Runtime, DateTimeOffset.UtcNow));

        // Separate keys per purpose: a runtime key cannot sign a catalog.
        var asCatalog = DocumentSigner.Sign(payload, TrustPurposes.Runtime, keys.RuntimeSeed, keys.RuntimeCert);
        asCatalog.Purpose = TrustPurposes.Catalog;
        Assert.Throws<TrustException>(() => store.Verify(asCatalog, TrustPurposes.Catalog, DateTimeOffset.UtcNow));

        var tampered = DocumentSigner.Sign(payload, TrustPurposes.Runtime, keys.RuntimeSeed, keys.RuntimeCert);
        tampered.Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"hello\":2}"));
        Assert.Throws<TrustException>(() => store.Verify(tampered, TrustPurposes.Runtime, DateTimeOffset.UtcNow));

        Assert.Throws<TrustException>(() => new TrustStore([TestKeys.Create().RootPublic]).Verify(document, TrustPurposes.Runtime, DateTimeOffset.UtcNow));
        Assert.Throws<TrustException>(() => store.Verify(document, TrustPurposes.Runtime, DateTimeOffset.UtcNow.AddYears(5)));
        Assert.Throws<TrustException>(() => new TrustStore([keys.RootPublic], [keys.RuntimeCert.KeyId]).Verify(document, TrustPurposes.Runtime, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SignedDocument_RejectsCertificateForgedWithoutRoot()
    {
        var keys = TestKeys.Create();
        byte[] attackerSeed = RandomNumberGenerator.GetBytes(32);
        var forged = DocumentSigner.Certify(attackerSeed, Ed25519.GetPublicKey(attackerSeed), [TrustPurposes.Runtime], DateTimeOffset.UtcNow.AddDays(1));
        forged.RootKeyId = TrustStore.KeyId(keys.RootPublic);
        var document = DocumentSigner.Sign([1, 2, 3], TrustPurposes.Runtime, attackerSeed, forged);

        Assert.Throws<TrustException>(() => new TrustStore([keys.RootPublic]).Verify(document, TrustPurposes.Runtime, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Freshness_RejectsRollbackAndExpiredDocuments()
    {
        string state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "sequences.json");
        var now = DateTimeOffset.UtcNow;
        Freshness.Check("release:stable", 10, now.AddDays(1), state, now, persist: true);
        Freshness.Check("release:stable", 10, now.AddDays(1), state, now, persist: true);

        Assert.Throws<TrustException>(() => Freshness.Check("release:stable", 9, now.AddDays(1), state, now, persist: true));
        Assert.Throws<TrustException>(() => Freshness.Check("release:stable", 11, now.AddSeconds(-1), state, now, persist: true));
        Freshness.Check("release:other", 1, now.AddDays(1), state, now, persist: true);
    }

    [Theory]
    [InlineData("1.2", new[] { "1.0" }, "1.0", null)]
    [InlineData("1.0", new[] { "1.3" }, "1.0", null)]
    [InlineData("2.1", new[] { "1.4", "2.3" }, "2.1", null)]
    [InlineData("3.0", new[] { "1.4", "2.3" }, null, ErrorCodes.ProtocolTooOld)]
    [InlineData("0.9", new[] { "1.4", "2.3" }, null, ErrorCodes.NotSupported)]
    public void ContractVersions_NegotiatesWithinMajor(string client, string[] server, string? expected, string? error)
    {
        Assert.Equal(expected, ContractVersions.Negotiate(client, server, out string? actualError));
        Assert.Equal(error, actualError);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.0.0-preview.2", "1.0.0-preview.10", -1)]
    [InlineData("1.0.0-preview.1", "1.0.0", -1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("1.0.0+abc", "1.0.0", 0)]
    public void SemVer_Orders(string a, string b, int expected) => Assert.Equal(expected, Math.Sign(SemVer.Compare(a, b)));

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("/evil.txt")]
    public void SafeZip_RejectsPathTraversal(string entryName)
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string zipPath = Path.Combine(dir, "evil.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
            writer.Write("x");
        }

        Assert.Throws<InvalidDataException>(() => SafeZip.ExtractToDirectory(zipPath, Path.Combine(dir, "out")));
        Assert.False(File.Exists(Path.Combine(dir, "evil.txt")));
    }

    [Fact]
    public void HostMatches_SupportsWildcardSubdomainsOnly()
    {
        string[] allow = ["huggingface.co", "*.hf.co"];
        Assert.True(SecureDownloader.HostMatches("huggingface.co", allow));
        Assert.True(SecureDownloader.HostMatches("cas-bridge.xethub.hf.co", allow));
        Assert.False(SecureDownloader.HostMatches("evilhuggingface.co", allow));
        Assert.False(SecureDownloader.HostMatches("hf.co.evil.com", allow));
    }
}

internal sealed record TestKeys(byte[] RootSeed, byte[] RootPublic, byte[] RuntimeSeed, KeyCertificate RuntimeCert)
{
    public static TestKeys Create()
    {
        byte[] root = RandomNumberGenerator.GetBytes(32);
        byte[] runtime = RandomNumberGenerator.GetBytes(32);
        var cert = DocumentSigner.Certify(root, Ed25519.GetPublicKey(runtime), [TrustPurposes.Runtime], DateTimeOffset.UtcNow.AddDays(30));
        return new TestKeys(root, Ed25519.GetPublicKey(root), runtime, cert);
    }
}
