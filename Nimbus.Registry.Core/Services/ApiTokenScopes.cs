using Microsoft.AspNetCore.Http;

namespace Nimbus.Registry.Services;

// The scope vocabulary, and which scope each route asks for.
//
// Coarse on purpose for v1: `whitelist:write` covers add and remove rather than splitting into
// `whitelist:add` and `whitelist:remove`. Nothing is lost by waiting, because scopes are
// additive strings: a finer pair can arrive later without invalidating a token issued under the
// coarse one. Splitting now would buy combinations to reason about and no caller that needs one
// half without the other.
public static class ApiTokenScopes
{
    public const string BansRead = "bans:read";
    public const string BansWrite = "bans:write";
    public const string WhitelistRead = "whitelist:read";
    public const string WhitelistWrite = "whitelist:write";
    public const string ServersRead = "servers:read";

    public static readonly IReadOnlyList<string> All = new[]
    {
        BansRead, BansWrite, WhitelistRead, WhitelistWrite, ServersRead,
    };

    public static bool IsKnown(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return false;
        foreach (var known in All)
            if (string.Equals(known, scope, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // The scope a bearer token needs to reach this route, or null when no scope reaches it at
    // all. Null is the answer for the internal endpoints (heartbeat, reservations, transfer
    // intents, token management itself) and for anything not named here, which keeps a route
    // added later closed to tokens until somebody decides what it should cost. That is the cap
    // on a leaked bot credential: moderation-list writes at rate-limit speed, and nothing that
    // moves a player or mints anything.
    public static string? RequiredScope(string method, PathString path)
    {
        // Trailing slashes and casing come from the caller, so neither is allowed to decide
        // whether a route is internal.
        string p = (path.Value ?? "").TrimEnd('/').ToLowerInvariant();
        bool get = HttpMethods.IsGet(method);
        bool post = HttpMethods.IsPost(method);

        return p switch
        {
            "/api/bans" when get => BansRead,
            "/api/bans" when post => BansWrite,
            "/api/bans/lift" when post => BansWrite,
            "/api/whitelist" when get => WhitelistRead,
            "/api/whitelist" when post => WhitelistWrite,
            "/api/whitelist/remove" when post => WhitelistWrite,
            "/api/servers" when get => ServersRead,
            _ => null,
        };
    }
}
