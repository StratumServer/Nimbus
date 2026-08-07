using System.Security.Cryptography;

namespace Nimbus.Shared;

// The network shared secret is the only credential between a backend and the registry: whoever
// knows it can heartbeat as any server and mint a reservation onto any backend. Shipping a literal
// default means every install that never edited it shares one publicly documented value, so the
// value is generated on the machine that needs it instead (#40).
public static class SecretGenerator
{
    // Letters and digits only, so the value survives a TOML string, a JSON string, a shell
    // variable and an env file without an escaping question, and nobody has to think about quoting
    // while copying it to a backend. 0, O, o, 1, l and I are left out: this secret's whole job is
    // to be transcribed into another config, sometimes off a panel screen, and a character pair
    // that only differs in a font is an authentication failure with no clue attached.
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    // 45 characters out of 56 is a shade over 260 bits, comfortably past the 256 HMAC-SHA256 uses.
    public const int Length = 45;

    // RandomNumberGenerator.GetString samples the alphabet without modulo bias, which a hand-rolled
    // "random byte % 62" would not.
    public static string NewSharedSecret() => RandomNumberGenerator.GetString(Alphabet, Length);
}
