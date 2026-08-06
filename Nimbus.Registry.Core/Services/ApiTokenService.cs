using Nimbus.Shared.Models;

namespace Nimbus.Registry.Services;

// Why a token request is refused. Statuses rather than HTTP results, the same shape
// ReservationService uses (#83): the endpoint maps a refusal to a status code and the proxy's
// in-process client maps it to a null and a log line, and neither of them re-decides what a
// valid token request looks like.
public enum ApiTokenCreateStatus
{
    Ok,
    MissingName,
    NoScopes,
    UnknownScope,
}

public sealed class ApiTokenCreateResult
{
    public ApiTokenCreateStatus Status { get; init; }

    // The plaintext, and the only copy of it that will ever exist. Empty on refusal.
    public string Plaintext { get; init; } = "";

    public ApiToken? Token { get; init; }

    // The scope that was not recognised, so the caller can name it rather than making the
    // operator guess which of five strings was the typo.
    public string UnknownScope { get; init; } = "";
}

// Minting, revoking and listing the scoped credentials. The rules live here rather than in the
// endpoint because embedded deployments reach them without passing through HTTP, and a rule that
// only one of the two paths enforces is a rule that half the deployments do not have.
public sealed class ApiTokenService
{
    // 90 days. An indefinitely-lived scoped token is the old API-key problem with a smaller blast
    // radius, and a revoke endpoint with no default TTL only revokes what somebody remembers to.
    // `permanent` is the way out, and it has to be asked for.
    public const int DefaultDurationSeconds = 90 * 24 * 60 * 60;

    private readonly ApiTokenStore _store;
    private readonly TimeProvider _clock;

    public ApiTokenService(ApiTokenStore store, TimeProvider? clock = null)
    {
        _store = store;
        _clock = clock ?? TimeProvider.System;
    }

    public ApiTokenCreateResult Create(ApiTokenCreateRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
            return new ApiTokenCreateResult { Status = ApiTokenCreateStatus.MissingName };

        // Every log line and every listing identifies a token by its name, so a token without one
        // is a credential nobody can talk about after it leaks.
        var scopes = new List<string>();
        foreach (var raw in request.Scopes)
        {
            var scope = (raw ?? "").Trim().ToLowerInvariant();
            if (scope.Length == 0) continue;
            // Refused rather than dropped: a typo silently removed leaves the operator holding a
            // token they believe can write bans and which cannot.
            if (!ApiTokenScopes.IsKnown(scope))
                return new ApiTokenCreateResult { Status = ApiTokenCreateStatus.UnknownScope, UnknownScope = scope };
            if (!scopes.Contains(scope)) scopes.Add(scope);
        }

        // A token carrying nothing authenticates and then fails every route, which reads like a
        // registry bug from the caller's side.
        if (scopes.Count == 0)
            return new ApiTokenCreateResult { Status = ApiTokenCreateStatus.NoScopes };

        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        string plaintext = ApiTokenSecret.NewSecret();
        var token = new ApiToken
        {
            Id = ApiTokenSecret.NewId(),
            Name = request.Name.Trim(),
            Hash = ApiTokenSecret.Hash(plaintext),
            Scopes = scopes,
            CreatedAtUnix = now,
            ExpiresAtUnix = Expiry(request, now),
            LastUsedAtUnix = 0,
            CreatedBy = request.CreatedBy ?? "",
            Revoked = false,
        };

        _store.Add(token);
        return new ApiTokenCreateResult { Status = ApiTokenCreateStatus.Ok, Plaintext = plaintext, Token = token };
    }

    public bool Revoke(string id) => _store.Revoke(id);

    // Redacted on the way out: the hash is not needed anywhere outside this process.
    public List<ApiToken> List() => _store.All().ConvertAll(static t => t.Redacted());

    // Zero only when it was asked for by name. A duration that is missing or nonsense falls back
    // to the default rather than to permanence, which is the direction a mistake should fall in.
    private static long Expiry(ApiTokenCreateRequest request, long nowUnix)
    {
        if (request.Permanent) return 0;
        int seconds = request.DurationSeconds > 0 ? request.DurationSeconds : DefaultDurationSeconds;
        return nowUnix + seconds;
    }
}
