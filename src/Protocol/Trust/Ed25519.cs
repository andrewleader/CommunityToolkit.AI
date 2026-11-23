using System.Numerics;
using System.Security.Cryptography;

namespace CommunityToolkit.AI.Protocol.Trust;

/// <summary>
/// Ed25519 (RFC 8032), dependency-free. Verification is what clients need; signing is used by the build tooling.
/// Signing here is not constant-time, so private keys must only be used on build machines, never on end-user machines.
/// </summary>
internal static class Ed25519
{
    public const int PublicKeySize = 32;
    public const int SignatureSize = 64;
    public const int SeedSize = 32;

    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger Q = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inv(121666));
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly Point G = CreateBasePoint();

    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    public static byte[] GetPublicKey(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedSize)
        {
            throw new ArgumentException("The seed must be 32 bytes.", nameof(seed));
        }

        var (a, _) = ExpandSecret(seed);
        return Compress(Multiply(a, G));
    }

    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        var (a, prefix) = ExpandSecret(seed);
        byte[] publicKey = Compress(Multiply(a, G));
        BigInteger r = HashModQ(prefix, message);
        byte[] rEncoded = Compress(Multiply(r, G));
        BigInteger h = HashModQ(rEncoded, publicKey, message);
        BigInteger s = Mod(r + h * a, Q);
        var signature = new byte[SignatureSize];
        rEncoded.CopyTo(signature, 0);
        ToLittleEndian(s).CopyTo(signature, 32);
        return signature;
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeySize || signature.Length != SignatureSize)
        {
            return false;
        }

        if (!TryDecompress(publicKey, out Point a))
        {
            return false;
        }

        ReadOnlySpan<byte> rEncoded = signature[..32];
        if (!TryDecompress(rEncoded, out Point r))
        {
            return false;
        }

        BigInteger s = FromLittleEndian(signature[32..]);
        if (s >= Q)
        {
            return false;
        }

        BigInteger h = HashModQ(rEncoded, publicKey, message);
        Point sB = Multiply(s, G);
        Point hA = Multiply(h, a);
        return PointEquals(sB, Add(r, hA));
    }

    private static Point CreateBasePoint()
    {
        BigInteger y = Mod(4 * Inv(5));
        BigInteger x = RecoverX(y, 0) ?? throw new InvalidOperationException();
        return new Point(x, y, 1, Mod(x * y));
    }

    private static (BigInteger A, byte[] Prefix) ExpandSecret(ReadOnlySpan<byte> seed)
    {
        byte[] h = SHA512.HashData(seed);
        BigInteger a = FromLittleEndian(h.AsSpan(0, 32));
        a &= (BigInteger.One << 254) - 8;
        a |= BigInteger.One << 254;
        return (a, h[32..]);
    }

    private static BigInteger HashModQ(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        sha.AppendData(a);
        sha.AppendData(b);
        return Mod(FromLittleEndian(sha.GetHashAndReset()), Q);
    }

    private static BigInteger HashModQ(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        sha.AppendData(a);
        sha.AppendData(b);
        sha.AppendData(c);
        return Mod(FromLittleEndian(sha.GetHashAndReset()), Q);
    }

    private static Point Add(Point p, Point q)
    {
        BigInteger a = Mod((p.Y - p.X) * (q.Y - q.X));
        BigInteger b = Mod((p.Y + p.X) * (q.Y + q.X));
        BigInteger c = Mod(2 * p.T * q.T * D);
        BigInteger d = Mod(2 * p.Z * q.Z);
        BigInteger e = b - a, f = d - c, g = d + c, h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Multiply(BigInteger s, Point p)
    {
        var q = new Point(0, 1, 1, 0);
        while (s > 0)
        {
            if (!s.IsEven)
            {
                q = Add(q, p);
            }

            p = Add(p, p);
            s >>= 1;
        }

        return q;
    }

    private static bool PointEquals(Point p, Point q) =>
        Mod(p.X * q.Z - q.X * p.Z).IsZero && Mod(p.Y * q.Z - q.Y * p.Z).IsZero;

    private static BigInteger? RecoverX(BigInteger y, int sign)
    {
        if (y >= P)
        {
            return null;
        }

        BigInteger x2 = Mod((y * y - 1) * Inv(D * y * y + 1));
        if (x2.IsZero)
        {
            return sign != 0 ? null : BigInteger.Zero;
        }

        BigInteger x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (!Mod(x * x - x2).IsZero)
        {
            x = Mod(x * SqrtM1);
        }

        if (!Mod(x * x - x2).IsZero)
        {
            return null;
        }

        if ((int)(x & 1) != sign)
        {
            x = P - x;
        }

        return x;
    }

    private static byte[] Compress(Point p)
    {
        BigInteger zInv = Inv(p.Z);
        BigInteger x = Mod(p.X * zInv);
        BigInteger y = Mod(p.Y * zInv);
        return ToLittleEndian(y | ((x & 1) << 255));
    }

    private static bool TryDecompress(ReadOnlySpan<byte> encoded, out Point point)
    {
        point = default;
        if (encoded.Length != 32)
        {
            return false;
        }

        BigInteger y = FromLittleEndian(encoded);
        int sign = (int)(y >> 255);
        y &= (BigInteger.One << 255) - 1;
        BigInteger? x = RecoverX(y, sign);
        if (x is null)
        {
            return false;
        }

        point = new Point(x.Value, y, 1, Mod(x.Value * y));
        return true;
    }

    private static BigInteger Mod(BigInteger value) => Mod(value, P);

    private static BigInteger Mod(BigInteger value, BigInteger m)
    {
        BigInteger r = BigInteger.Remainder(value, m);
        return r.Sign < 0 ? r + m : r;
    }

    private static BigInteger Inv(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

    private static BigInteger FromLittleEndian(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: false);

    private static byte[] ToLittleEndian(BigInteger value)
    {
        var result = new byte[32];
        if (!value.TryWriteBytes(result, out _, isUnsigned: true, isBigEndian: false))
        {
            throw new InvalidOperationException("Value does not fit in 32 bytes.");
        }

        return result;
    }
}
