using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunityToolkit.AI.Protocol.Trust;

internal static class TrustPurposes
{
    public const string Runtime = "runtime";
    public const string Catalog = "catalog";
    public const string Models = "models";
}

/// <summary>
/// A signed JSON document: the payload bytes, signatures by signing keys, and the certificates that bind each
/// signing key to an offline root key for a set of purposes.
/// </summary>
internal sealed class SignedDocument
{
    [JsonPropertyName("purpose")] public string Purpose { get; set; } = "";
    [JsonPropertyName("payload")] public string Payload { get; set; } = "";
    [JsonPropertyName("signatures")] public List<DocumentSignature> Signatures { get; set; } = new();
    [JsonPropertyName("certificates")] public List<KeyCertificate> Certificates { get; set; } = new();
}

internal sealed class DocumentSignature
{
    [JsonPropertyName("keyId")] public string KeyId { get; set; } = "";
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
}

internal sealed class KeyCertificate
{
    [JsonPropertyName("keyId")] public string KeyId { get; set; } = "";
    [JsonPropertyName("publicKey")] public string PublicKey { get; set; } = "";
    [JsonPropertyName("purposes")] public List<string> Purposes { get; set; } = new();
    [JsonPropertyName("notAfter")] public DateTimeOffset NotAfter { get; set; }
    [JsonPropertyName("rootKeyId")] public string RootKeyId { get; set; } = "";
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";

    public byte[] GetSignedBytes()
    {
        var purposes = Purposes.OrderBy(p => p, StringComparer.Ordinal);
        return Encoding.UTF8.GetBytes($"localai-cert-v1\n{KeyId}\n{PublicKey}\n{string.Join(",", purposes)}\n{NotAfter.UtcDateTime:O}\n{RootKeyId}");
    }
}

internal sealed class TrustException : Exception
{
    public TrustException(string message)
        : base(message)
    {
    }
}

/// <summary>Verifies signed documents against a set of trusted root keys and a revocation list.</summary>
internal sealed class TrustStore
{
    private readonly Dictionary<string, byte[]> _roots = new(StringComparer.Ordinal);
    private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);

    public TrustStore(IEnumerable<byte[]> rootKeys, IEnumerable<string>? revokedKeyIds = null)
    {
        foreach (var key in rootKeys)
        {
            if (key.Length == Ed25519.PublicKeySize)
            {
                _roots[KeyId(key)] = key;
            }
        }

        if (revokedKeyIds is not null)
        {
            foreach (var id in revokedKeyIds)
            {
                _revoked.Add(id);
            }
        }
    }

    public IReadOnlyCollection<string> RevokedKeyIds => _revoked;

    public void Revoke(IEnumerable<string> keyIds)
    {
        foreach (var id in keyIds)
        {
            _revoked.Add(id);
        }
    }

    public static string KeyId(ReadOnlySpan<byte> publicKey) => Convert.ToHexString(SHA256.HashData(publicKey)).Substring(0, 16).ToLowerInvariant();

    public static byte[] GetDocumentSignedBytes(string purpose, ReadOnlySpan<byte> payload)
    {
        byte[] prefix = Encoding.UTF8.GetBytes($"localai-doc-v1\n{purpose}\n");
        var result = new byte[prefix.Length + payload.Length];
        prefix.CopyTo(result, 0);
        payload.CopyTo(result.AsSpan(prefix.Length));
        return result;
    }

    public static SignedDocument Parse(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(json, TrustJsonContext.Default.SignedDocument) ?? throw new TrustException("The signed document is empty.");

    /// <summary>Verifies the document for the given purpose and returns its payload bytes.</summary>
    public byte[] Verify(SignedDocument document, string purpose, DateTimeOffset now)
    {
        if (!string.Equals(document.Purpose, purpose, StringComparison.Ordinal))
        {
            throw new TrustException($"Expected a '{purpose}' document but got '{document.Purpose}'.");
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(document.Payload);
        }
        catch (FormatException)
        {
            throw new TrustException("The payload is not valid base64.");
        }

        byte[] signedBytes = GetDocumentSignedBytes(purpose, payload);
        foreach (var signature in document.Signatures)
        {
            if (_revoked.Contains(signature.KeyId))
            {
                continue;
            }

            var certificate = document.Certificates.FirstOrDefault(c => c.KeyId == signature.KeyId);
            if (certificate is null || !IsCertificateValid(certificate, purpose, now, out byte[] signingKey))
            {
                continue;
            }

            if (TryFromBase64(signature.Signature, out byte[] sig) && Ed25519.Verify(signingKey, signedBytes, sig))
            {
                return payload;
            }
        }

        throw new TrustException($"No valid signature from a trusted '{purpose}' key.");
    }

    private bool IsCertificateValid(KeyCertificate certificate, string purpose, DateTimeOffset now, out byte[] signingKey)
    {
        signingKey = [];
        if (certificate.NotAfter < now || !certificate.Purposes.Contains(purpose) || _revoked.Contains(certificate.RootKeyId))
        {
            return false;
        }

        if (!_roots.TryGetValue(certificate.RootKeyId, out byte[]? root))
        {
            return false;
        }

        if (!TryFromBase64(certificate.PublicKey, out signingKey) || signingKey.Length != Ed25519.PublicKeySize || KeyId(signingKey) != certificate.KeyId)
        {
            return false;
        }

        return TryFromBase64(certificate.Signature, out byte[] certSig) && Ed25519.Verify(root, certificate.GetSignedBytes(), certSig);
    }

    private static bool TryFromBase64(string text, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(text);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}

/// <summary>Build-time signing. Not used by clients.</summary>
internal static class DocumentSigner
{
    public static KeyCertificate Certify(byte[] rootSeed, byte[] signingPublicKey, IEnumerable<string> purposes, DateTimeOffset notAfter)
    {
        var certificate = new KeyCertificate
        {
            KeyId = TrustStore.KeyId(signingPublicKey),
            PublicKey = Convert.ToBase64String(signingPublicKey),
            Purposes = purposes.OrderBy(p => p, StringComparer.Ordinal).ToList(),
            NotAfter = notAfter.ToUniversalTime(),
            RootKeyId = TrustStore.KeyId(Ed25519.GetPublicKey(rootSeed)),
        };
        certificate.Signature = Convert.ToBase64String(Ed25519.Sign(rootSeed, certificate.GetSignedBytes()));
        return certificate;
    }

    public static SignedDocument Sign(byte[] payload, string purpose, byte[] signingSeed, KeyCertificate certificate)
    {
        byte[] publicKey = Ed25519.GetPublicKey(signingSeed);
        if (TrustStore.KeyId(publicKey) != certificate.KeyId)
        {
            throw new InvalidOperationException("The certificate does not match the signing key.");
        }

        return new SignedDocument
        {
            Purpose = purpose,
            Payload = Convert.ToBase64String(payload),
            Signatures = [new DocumentSignature { KeyId = certificate.KeyId, Signature = Convert.ToBase64String(Ed25519.Sign(signingSeed, TrustStore.GetDocumentSignedBytes(purpose, payload))) }],
            Certificates = [certificate],
        };
    }
}
