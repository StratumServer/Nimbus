namespace Nimbus.Shared.Models;

// A scoped credential a third party authenticates with, held by the registry (#54). It sits
// next to NetworkBan and WhitelistEntry rather than inside the registry because it crosses the
// HTTP boundary in both directions: the registry writes it, the proxy reads it back through
// IRegistryClient, and nimctl prints it.
//
// The secret itself is not in here and never is. What is stored is its SHA-256, so a leaked
// state file or a leaked backup exposes nothing that can be replayed against the registry: the
// plaintext exists exactly once, in the response that created it.
public sealed record ApiToken
{
    // Short, non-secret handle. This is what `token revoke` names and what log lines carry, so
    // it has to be safe to write down and useless to anyone who copies it.
    public string Id { get; set; } = "";

    // Operator-chosen label, and the attribution a token-authenticated write is stamped with
    // ("token:<name>" in BannedBy / AddedBy).
    public string Name { get; set; } = "";

    // Lowercase hex SHA-256 of the whole presented string, `nsk_` prefix included. Validation is
    // one hash and one dictionary lookup, so a busy list costs the same as an empty one.
    // Redacted out of every HTTP response by Redacted(): nothing downstream needs it.
    public string Hash { get; set; } = "";

    public List<string> Scopes { get; set; } = new();

    public long CreatedAtUnix { get; set; }

    // 0 means permanent, which is the explicit opt-out rather than the default. Anything else is
    // the second the token stops authenticating.
    public long ExpiresAtUnix { get; set; }

    // Telemetry, not truth: it says when the credential was last seen, and it is written back to
    // disk at most once a minute per token so a chatty bot does not turn every call into a file
    // rewrite. A reading a few seconds stale costs nothing; a reading that is missing entirely
    // costs the operator the one signal that says which token to revoke.
    public long LastUsedAtUnix { get; set; }

    public string CreatedBy { get; set; } = "";

    // Revoked tokens are kept rather than deleted. The record is the audit trail, and a hash that
    // is still in the file cannot be handed out again by accident.
    public bool Revoked { get; set; }

    public bool IsExpiredAt(long nowUnix) => ExpiresAtUnix > 0 && nowUnix >= ExpiresAtUnix;

    public bool IsUsableAt(long nowUnix) => !Revoked && !IsExpiredAt(nowUnix);

    // Scope names are lowercase by convention and compared without case so a token created with
    // "Bans:Write" is not quietly useless.
    public bool HasScope(string scope)
    {
        foreach (var held in Scopes)
            if (string.Equals(held, scope, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // The copy that goes on the wire. The hash is not a secret in the sense the token is, but
    // publishing it buys nobody anything, so responses carry the record with that one field
    // emptied instead of a second near-identical type.
    public ApiToken Redacted() => this with { Hash = "" };
}

// Body of POST /api/tokens.
public sealed class ApiTokenCreateRequest
{
    public string Name { get; set; } = "";

    public List<string> Scopes { get; set; } = new();

    // 0 or less means the default, which is 90 days. Ignored when Permanent is set.
    public int DurationSeconds { get; set; }

    // The explicit opt-out from expiry. An indefinitely-lived scoped token is still the old
    // API-key problem, so it has to be asked for by name.
    public bool Permanent { get; set; }

    public string CreatedBy { get; set; } = "";
}

// The one and only time the plaintext exists outside the caller that asked for it.
public sealed class ApiTokenCreateResponse
{
    public bool Ok { get; set; }

    // The full `nsk_...` string. Never stored, never logged, never in any other response.
    public string Token { get; set; } = "";

    public ApiToken? Record { get; set; }

    public string? Error { get; set; }
}

// Body of POST /api/tokens/revoke. The id travels in the signed body rather than the query
// because the HMAC covers method, path and body and never the query string (#69).
public sealed class ApiTokenRevokeRequest
{
    public string Id { get; set; } = "";
}

public sealed class ApiTokenResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

public sealed class ApiTokenListResponse
{
    public bool Ok { get; set; }
    public List<ApiToken> Tokens { get; set; } = new();
}
