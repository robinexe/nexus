using System.Numerics;
using System.Security.Cryptography;

namespace Nexus.NetRoute.WireGuard;

/// <summary>
/// X25519 (RFC 7748, seção 5), o mesmo das chaves do WireGuard. Implementado aqui para não trazer pacote novo: só é usado para
/// gerar o par de chaves do PC uma vez (velocidade não importa), e os vetores de teste do RFC estão nos testes. A criptografia
/// do túnel em si é a do WireGuard oficial.
/// </summary>
public static class Curve25519
{
    public const int KeySize = 32;
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger A24 = 121665;
    private static readonly byte[] BasePoint = Base();

    private static byte[] Base()
    {
        var b = new byte[KeySize];
        b[0] = 9;
        return b;
    }

    /// <summary>Escalar com os bits ajustados (clamping): é a forma em que o WireGuard guarda a chave privada.</summary>
    public static byte[] Clamp(ReadOnlySpan<byte> scalar)
    {
        if (scalar.Length != KeySize) throw new ArgumentException("Chave precisa de 32 bytes.", nameof(scalar));
        var k = scalar.ToArray();
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;
        return k;
    }

    public static byte[] GeneratePrivateKey()
    {
        var raw = RandomNumberGenerator.GetBytes(KeySize);
        try { return Clamp(raw); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    public static byte[] PublicKey(ReadOnlySpan<byte> privateKey) => ScalarMult(privateKey, BasePoint);

    /// <summary>X25519(k, u). O escalar é ajustado (clamping) aqui, como manda o RFC.</summary>
    public static byte[] ScalarMult(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> uCoordinate)
    {
        if (uCoordinate.Length != KeySize) throw new ArgumentException("Coordenada precisa de 32 bytes.", nameof(uCoordinate));
        var kBytes = Clamp(scalar);
        var k = new BigInteger(kBytes, isUnsigned: true, isBigEndian: false);
        var uBytes = uCoordinate.ToArray();
        uBytes[31] &= 127;
        var x1 = Mod(new BigInteger(uBytes, isUnsigned: true, isBigEndian: false));

        BigInteger x2 = 1, z2 = 0, x3 = x1, z3 = 1;
        var swap = 0;
        for (var t = 254; t >= 0; t--)
        {
            var kt = (int)((k >> t) & 1);
            swap ^= kt;
            if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
            swap = kt;

            var a = Mod(x2 + z2);
            var aa = Mod(a * a);
            var b = Mod(x2 - z2);
            var bb = Mod(b * b);
            var e = Mod(aa - bb);
            var c = Mod(x3 + z3);
            var d = Mod(x3 - z3);
            var da = Mod(d * a);
            var cb = Mod(c * b);
            var sum = Mod(da + cb);
            var diff = Mod(da - cb);
            x3 = Mod(sum * sum);
            z3 = Mod(x1 * Mod(diff * diff));
            x2 = Mod(aa * bb);
            z2 = Mod(e * Mod(aa + A24 * e));
        }
        if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
        var result = Mod(x2 * BigInteger.ModPow(z2, P - 2, P));
        var bytes = result.ToByteArray(isUnsigned: true, isBigEndian: false);
        var output = new byte[KeySize];
        bytes.AsSpan(0, Math.Min(bytes.Length, KeySize)).CopyTo(output);
        return output;
    }

    private static BigInteger Mod(BigInteger v)
    {
        var r = BigInteger.Remainder(v, P);
        return r.Sign < 0 ? r + P : r;
    }
}

/// <summary>Chaves no formato do WireGuard: 32 bytes em Base64 padrão (44 caracteres terminando em '=').</summary>
public static class WireGuardKey
{
    public static bool IsValid(string? text)
    {
        if (text is null || text.Length != 44 || !text.EndsWith('=')) return false;
        Span<byte> buffer = stackalloc byte[33];
        return Convert.TryFromBase64String(text, buffer, out var written) && written == Curve25519.KeySize;
    }

    public static string Encode(ReadOnlySpan<byte> key) =>
        key.Length == Curve25519.KeySize ? Convert.ToBase64String(key) : throw new ArgumentException("Chave precisa de 32 bytes.", nameof(key));

    public static byte[] Decode(string text) =>
        IsValid(text) ? Convert.FromBase64String(text) : throw new FormatException("Chave WireGuard inválida (esperado Base64 de 32 bytes).");

    public static (string PrivateKey, string PublicKey) GenerateKeyPair()
    {
        var priv = Curve25519.GeneratePrivateKey();
        try { return (Encode(priv), Encode(Curve25519.PublicKey(priv))); }
        finally { CryptographicOperations.ZeroMemory(priv); }
    }

    public static string PublicFromPrivate(string privateKey)
    {
        var priv = Decode(privateKey);
        try { return Encode(Curve25519.PublicKey(priv)); }
        finally { CryptographicOperations.ZeroMemory(priv); }
    }
}
