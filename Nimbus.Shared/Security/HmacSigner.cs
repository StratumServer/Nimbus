using System.Security.Cryptography;
using System.Text;

namespace Nimbus.Shared.Security;

// HMAC-SHA256 signer for Nimbus control-plane requests. Signs the canonical request line
// (method + path + protocol + timestamp + nonce + body) with the per-network shared secret.
public static class HmacSigner
{
    // Canonical string-to-sign for a Nimbus request.
    // Format: METHOD\nPATH\nPROTOCOL\nTIMESTAMP\nNONCE\nSHA256(body)
    public static string CanonicalString(string method, string path, int protocolVersion, long timestampUnix, string nonce, ReadOnlySpan<byte> body)
    {
        Span<byte> bodyHash = stackalloc byte[32];
        SHA256.HashData(body, bodyHash);
        return string.Concat(
            method.ToUpperInvariant(), "\n",
            path, "\n",
            protocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), "\n",
            timestampUnix.ToString(System.Globalization.CultureInfo.InvariantCulture), "\n",
            nonce, "\n",
            Convert.ToHexString(bodyHash));
    }

    // HMAC-SHA256 of canonical with the shared secret, hex-encoded.
    public static string Sign(string sharedSecret, string canonical)
    {
        byte[] key = Encoding.UTF8.GetBytes(sharedSecret);
        byte[] data = Encoding.UTF8.GetBytes(canonical);
        byte[] mac = HMACSHA256.HashData(key, data);
        return Convert.ToHexString(mac);
    }

    // Constant-time signature compare. Returns true only if signatures match and the timestamp
    // is within NimbusProtocol.MaxClockSkewSeconds of nowUnix.
    public static bool Verify(string sharedSecret, string canonical, string providedSignatureHex, long timestampUnix, long nowUnix)
    {
        long skew = Math.Abs(nowUnix - timestampUnix);
        if (skew > NimbusProtocol.MaxClockSkewSeconds) return false;

        string expected = Sign(sharedSecret, canonical);
        if (expected.Length != providedSignatureHex.Length) return false;

        ReadOnlySpan<byte> a = Encoding.ASCII.GetBytes(expected);
        ReadOnlySpan<byte> b = Encoding.ASCII.GetBytes(providedSignatureHex);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    // Random URL-safe nonce.
    public static string NewNonce()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
