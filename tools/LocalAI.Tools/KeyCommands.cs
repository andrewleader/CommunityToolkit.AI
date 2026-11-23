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
        byte[] rootSeed = ReadSeed(a.Required("root-key"));
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
        byte[] seed = ReadSeed(a.Required("key"));
        var cert = ReadCertificate(a.Required("cert"));
        if (TrustStore.KeyId(Ed25519.GetPublicKey(seed)) != cert.KeyId)
        {
            throw new ToolException($"The key in {a.Required("key")} does not match certificate {cert.KeyId}. Check the key and certificate secrets are a matching pair.");
        }

        if (!cert.Purposes.Contains(purpose))
        {
            throw new ToolException($"Certificate {cert.KeyId} is for [{string.Join(", ", cert.Purposes)}], not '{purpose}'.");
        }

        if (cert.NotAfter < DateTimeOffset.UtcNow)
        {
            throw new ToolException($"Certificate {cert.KeyId} expired on {cert.NotAfter:yyyy-MM-dd}. Certify a new signing key with the root key.");
        }

        var document = DocumentSigner.Sign(payload, purpose, seed, cert);
        File.WriteAllText(a.Required("out"), JsonSerializer.Serialize(document, TrustJsonContext.Default.SignedDocument));
        Console.WriteLine($"Signed {a.Required("in")} as '{purpose}' with key {cert.KeyId}.");
        return 0;
    }

    private static byte[] ReadSeed(string source)
    {
        try
        {
            byte[] seed = Convert.FromBase64String(Args.ReadSecret(source));
            return seed.Length == Ed25519.SeedSize ? seed : throw new ToolException($"The key from {source} is {seed.Length} bytes; expected {Ed25519.SeedSize} (the base64 contents of a *.key file).");
        }
        catch (FormatException)
        {
            throw new ToolException($"The key from {source} is not base64. Use the contents of a *.key file from keygen.");
        }
    }

    private static KeyCertificate ReadCertificate(string source)
    {
        try
        {
            return JsonSerializer.Deserialize(Args.ReadSecret(source), TrustJsonContext.Default.KeyCertificate)
                ?? throw new ToolException($"The certificate from {source} is empty.");
        }
        catch (JsonException ex)
        {
            throw new ToolException($"The certificate from {source} is not valid JSON ({ex.Message}). Use the full contents of a *.cert.json file.");
        }
    }
}
