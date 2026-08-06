using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Nimbus.Registry.Services;

// Minting and hashing for the scoped credentials of #54. Separate from the store so the one
// place that produces a secret is also the one place that decides how it is recognised and how
// it is hashed; a generator and a verifier that disagree fail open.
public static class ApiTokenSecret
{
    // Visible prefix so a token pasted into a config, a log or a commit is identifiable on
    // sight, by a human or by a secret scanner, and revocable before anyone has to reason about
    // what the string is. `ghp_` and `sk_live_` exist for the same reason.
    public const string Prefix = "nsk_";

    // 32 bytes from the CSPRNG. 62^43 is just above 2^256, so 43 base62 characters carry the
    // whole draw with nothing folded away.
    public const int SecretBytes = 32;
    public const int SecretChars = 43;

    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    // Base62 rather than base64: the token ends up in shell commands, TOML files and URLs, and
    // `+`, `/` and `=` are the characters that get mangled, quoted or truncated on the way.
    public static string NewSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretBytes);
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var chars = new char[SecretChars];
        for (int i = SecretChars - 1; i >= 0; i--)
        {
            value = BigInteger.DivRem(value, 62, out var remainder);
            chars[i] = Alphabet[(int)remainder];
        }
        return Prefix + new string(chars);
    }

    // The handle, not a credential: it names a token in a listing, in a revoke and in a log line
    // where the secret must never appear. Random rather than sequential so a count of issued
    // tokens is not readable off one of them.
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    // Over the whole presented string, prefix included, so a hash cannot be matched by a token
    // that only agrees on the random part.
    public static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? ""))).ToLowerInvariant();

    // Whether a credential is even shaped like one of ours. A bearer header carrying something
    // else belongs to somebody else's middleware, so it is left alone rather than refused.
    public static bool LooksLikeToken(string? presented)
        => presented is not null && presented.StartsWith(Prefix, StringComparison.Ordinal);
}
