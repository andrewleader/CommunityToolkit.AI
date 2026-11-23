using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.AI.Protocol.Trust;

namespace CommunityToolkit.AI.Tools;

internal static class KeyCommands
{
    private static readonly string[] SigningPurposes = [TrustPurposes.Runtime, TrustPurposes.Catalog, TrustPurposes.Models];

    /// <summary>Creates a root key and one certified signing key per purpose (separate keys for runtime, catalog and models).</summary>
    public static int Keygen(Args a)
    {
        string dir = a.Required("out");
        int days = a.Int("valid-days", 365);
        Directory.CreateDirectory(dir);
        byte[] rootSeed = RandomNumberGenerator.GetBytes(Ed25519.SeedSize);
        byte[] rootPublic = Ed25519.GetPublicKey(rootSeed);
        File.WriteAllText(Path.Combine(dir, "root.key"), Convert.ToBase64String(rootSeed));
        File.WriteAllText(Path.Combine(dir, "root.pub"), Convert.ToBase64String(rootPublic));
        Console.WriteLine($"root     {TrustStore.KeyId(rootPublic)}  {Convert.ToBase64String(rootPublic)}");

        foreach (var purpose in SigningPurposes)
        {
            byte[] seed = RandomNumberGenerator.GetBytes(Ed25519.SeedSize);
            byte[] publicKey = Ed25519.GetPublicKey(seed);
            var cert = DocumentSigner.Certify(rootSeed, publicKey, [purpose], DateTimeOffset.UtcNow.AddDays(days));
            File.WriteAllText(Path.Combine(dir, $"{purpose}.key"), Convert.ToBase64String(seed));
            File.WriteAllText(Path.Combine(dir, $"{purpose}.cert.json"), JsonSerializer.Serialize(cert, TrustJsonContext.Default.KeyCertificate));
            Console.WriteLine($"{purpose,-8} {cert.KeyId}  valid until {cert.NotAfter:yyyy-MM-dd}");
        }

        Console.WriteLine($"Keys written to {dir}. Keep root.key offline.");
        return 0;
    }

    public static int Certify(Args a)
    {
        byte[] rootSeed = Convert.FromBase64String(Args.ReadSecret(a.Required("root-key")));
        byte[] publicKey = Convert.FromBase64String(a.Required("public-key"));
        var cert = DocumentSigner.Certify(rootSeed, publicKey, a.List("purposes"), DateTimeOffset.UtcNow.AddDays(a.Int("valid-days", 365)));
        File.WriteAllText(a.Required("out"), JsonSerializer.Serialize(cert, TrustJsonContext.Default.KeyCertificate));
        Console.WriteLine($"Certified {cert.KeyId} for [{string.Join(", ", cert.Purposes)}] until {cert.NotAfter:yyyy-MM-dd}.");
        return 0;
    }

    public static int Sign(Args a)
    {
        string purpose = a.Required("purpose");
        byte[] payload = File.ReadAllBytes(a.Required("in"));
        byte[] seed = Convert.FromBase64String(Args.ReadSecret(a.Required("key")));
        var cert = JsonSerializer.Deserialize(Args.ReadSecret(a.Required("cert")), TrustJsonContext.Default.KeyCertificate) ?? throw new ToolException("Invalid certificate.");
        var document = DocumentSigner.Sign(payload, purpose, seed, cert);
        File.WriteAllText(a.Required("out"), JsonSerializer.Serialize(document, TrustJsonContext.Default.SignedDocument));
        Console.WriteLine($"Signed {a.Required("in")} as '{purpose}' with key {cert.KeyId}.");
        return 0;
    }
}
