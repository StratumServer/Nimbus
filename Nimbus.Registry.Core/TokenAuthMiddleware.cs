using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Nimbus.Registry.Services;
using Nimbus.Shared.Models;

namespace Nimbus.Registry;

// Where a token-authenticated request leaves its identity, and how the endpoints read it back.
public static class ApiTokenIdentity
{
    public const string ItemsKey = "nimbus.api-token";

    public static ApiToken? Of(HttpContext ctx)
        => ctx.Items.TryGetValue(ItemsKey, out var value) ? value as ApiToken : null;

    // What a write made with this credential is stamped with. Null for an HMAC-authenticated
    // request, which keeps attributing itself the way it always has.
    public static string? Attribution(HttpContext ctx)
    {
        var token = Of(ctx);
        return token is null ? null : "token:" + token.Name;
    }
}

// Scoped bearer tokens for third-party integrations (#54), in front of HmacAuthMiddleware.
//
// The shape of the whole thing is decided by one rule: a request with no `Authorization: Bearer
// nsk_...` header is not touched. It falls through to the HMAC middleware exactly as before, so
// every backend, proxy and nimctl in existence is unaffected and the protocol version does not
// move. A request that does carry one never reaches the HMAC middleware, whether it is accepted
// or refused, because falling through after a refusal would answer a bad token with a complaint
// about missing signature headers.
//
// HMAC stays the credential for the components Nimbus ships, which sign every call and need
// replay protection on a possibly plain-HTTP link. Bearer is for the callers who will never
// implement canonical-string signing, and it buys its simplicity by depending entirely on the
// transport, which is why the gate below is the first thing that runs.
public sealed class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RegistryConfig _cfg;
    private readonly ApiTokenStore _tokens;
    private readonly ApiTokenRateLimiter _limiter;
    private readonly TimeProvider _clock;
    private readonly ILogger<TokenAuthMiddleware> _log;

    public TokenAuthMiddleware(RequestDelegate next, RegistryConfig cfg, ApiTokenStore tokens,
        ApiTokenRateLimiter limiter, TimeProvider clock, ILogger<TokenAuthMiddleware> log)
    {
        _next = next; _cfg = cfg; _tokens = tokens; _limiter = limiter; _clock = clock; _log = log;
    }

    public async Task Invoke(HttpContext ctx)
    {
        if (!ctx.Request.Path.StartsWithSegments("/api") || !TryReadBearer(ctx, out string presented))
        {
            await _next(ctx);
            return;
        }

        if (!_cfg.ApiTokens.Enabled)
        {
            await Refuse(ctx, StatusCodes.Status401Unauthorized, "api token authentication is disabled", null);
            return;
        }

        // Transport first, before anything looks at the credential. A token presented over a
        // connection that could be read off the wire is already spent, and answering it with
        // "unknown token" would be telling the eavesdropper whether it was worth keeping.
        if (!IsTransportAcceptable(ctx))
        {
            await Refuse(ctx, StatusCodes.Status403Forbidden,
                "api tokens require a loopback connection or TLS", null);
            return;
        }

        // Resolved before the lookup so that a bearer header on an internal endpoint is a 403
        // whatever the token turns out to be, valid, unknown or garbage. Nothing about the
        // credential is readable off the answer, and heartbeat, reservations, transfer intents
        // and token management stay HMAC-only by construction rather than by scope arithmetic.
        string? required = ApiTokenScopes.RequiredScope(ctx.Request.Method, ctx.Request.Path);
        if (required is null)
        {
            await Refuse(ctx, StatusCodes.Status403Forbidden,
                "this endpoint accepts HMAC authentication only", null);
            return;
        }

        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        var token = _tokens.FindByHash(ApiTokenSecret.Hash(presented));
        if (token is null)
        {
            await Refuse(ctx, StatusCodes.Status401Unauthorized, "unknown token", null);
            return;
        }

        if (token.Revoked)
        {
            await Refuse(ctx, StatusCodes.Status401Unauthorized, "token revoked", token);
            return;
        }

        if (token.IsExpiredAt(now))
        {
            await Refuse(ctx, StatusCodes.Status401Unauthorized, "token expired", token);
            return;
        }

        if (!token.HasScope(required))
        {
            await Refuse(ctx, StatusCodes.Status403Forbidden, $"token is missing scope '{required}'", token);
            return;
        }

        if (!_limiter.TryTake(token.Id, out int retryAfter))
        {
            ctx.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await Refuse(ctx, StatusCodes.Status429TooManyRequests, "rate limit exceeded", token);
            return;
        }

        // The identity the endpoints stamp their writes with, and the signal HmacAuthMiddleware
        // reads to let an already-authenticated request past without signature headers.
        ctx.Items[ApiTokenIdentity.ItemsKey] = token;
        _tokens.RecordUse(token, now);
        await _next(ctx);
    }

    // A bearer credential that is not shaped like ours belongs to somebody else's middleware, so
    // it is left alone rather than refused: this is the only reason an existing deployment cannot
    // be broken by turning the feature on.
    private static bool TryReadBearer(HttpContext ctx, out string presented)
    {
        presented = "";
        if (!ctx.Request.Headers.TryGetValue("Authorization", out StringValues values)) return false;

        string header = values.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;

        string candidate = header[scheme.Length..].Trim();
        if (!ApiTokenSecret.LooksLikeToken(candidate)) return false;

        presented = candidate;
        return true;
    }

    // "The scheme says https" and "the connection is protected" are not the same statement when
    // something else terminated the TLS. The two things this process can actually vouch for are
    // that the peer is on this machine, and that Kestrel itself did the handshake.
    private bool IsTransportAcceptable(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        // No peer address at all is an in-process or unix-socket caller, which is as local as
        // loopback gets.
        if (remote is null) return true;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote)) return true;

        // True only when this registry's own listener negotiated the TLS.
        if (ctx.Request.IsHttps) return true;

        if (!_cfg.ApiTokens.TrustForwardedProto) return false;

        // A header, and therefore only as trustworthy as the deployment that swore nothing else
        // can reach this bind. First hop wins: a proxy chain appends, so the entry that describes
        // the client's own connection is the leftmost one.
        string forwarded = ctx.Request.Headers["X-Forwarded-Proto"].ToString();
        int comma = forwarded.IndexOf(',');
        if (comma >= 0) forwarded = forwarded[..comma];
        return string.Equals(forwarded.Trim(), "https", StringComparison.OrdinalIgnoreCase);
    }

    // Terse bodies, and never the presented string. The log line carries the token's name and id,
    // which is what an operator revokes by; a refusal with no token behind it has neither, and
    // inventing a prefix of the secret to make the line look complete would put part of the
    // credential in the log.
    private async Task Refuse(HttpContext ctx, int status, string reason, ApiToken? token)
    {
        _log.LogWarning("api token reject {Path} from {Ip}: {Reason} (token {Name} id={Id})",
            ctx.Request.Path, ctx.Connection.RemoteIpAddress, reason,
            token?.Name ?? "<none>", token?.Id ?? "<none>");
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain";
        // CancellationToken.None for the same reason HmacAuthMiddleware uses it: a caller that
        // hangs up mid-write must not surface here as a cancellation.
        await ctx.Response.WriteAsync(reason, CancellationToken.None);
    }
}
