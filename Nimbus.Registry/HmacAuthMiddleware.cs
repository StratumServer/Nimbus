using Microsoft.AspNetCore.Http;
using Nimbus.Registry.Services;
using Nimbus.Shared;
using Nimbus.Shared.Security;

namespace Nimbus.Registry;

// Validates Nimbus HMAC headers and body signature on every request under /api. Rejects with
// 401 on bad signature, missing headers, or replays.
public sealed class HmacAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RegistryConfig _cfg;
    private readonly NonceCache _nonces;
    private readonly ILogger<HmacAuthMiddleware> _log;

    public HmacAuthMiddleware(RequestDelegate next, RegistryConfig cfg, NonceCache nonces, ILogger<HmacAuthMiddleware> log)
    {
        _next = next; _cfg = cfg; _nonces = nonces; _log = log;
    }

    public async Task Invoke(HttpContext ctx)
    {
        if (!ctx.Request.Path.StartsWithSegments("/api"))
        {
            await _next(ctx);
            return;
        }

        if (!ctx.Request.Headers.TryGetValue(NimbusProtocol.SignatureHeader, out var sigVals)
            || !ctx.Request.Headers.TryGetValue(NimbusProtocol.TimestampHeader, out var tsVals)
            || !ctx.Request.Headers.TryGetValue(NimbusProtocol.NonceHeader, out var nonceVals)
            || !ctx.Request.Headers.TryGetValue(NimbusProtocol.ProtocolHeader, out var protoVals))
        {
            await Reject(ctx, "missing nimbus headers");
            return;
        }

        if (!long.TryParse(tsVals.ToString(), out long ts)
            || !int.TryParse(protoVals.ToString(), out int proto))
        {
            await Reject(ctx, "malformed nimbus headers");
            return;
        }

        if (proto != NimbusProtocol.ProtocolVersion)
        {
            await Reject(ctx, $"protocol mismatch (got {proto}, expected {NimbusProtocol.ProtocolVersion})");
            return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - ts) > NimbusProtocol.MaxClockSkewSeconds)
        {
            await Reject(ctx, "clock skew");
            return;
        }

        string nonce = nonceVals.ToString();
        if (!_nonces.TryRecord(nonce, now))
        {
            await Reject(ctx, "replay");
            return;
        }

        // Buffer body to hash it and re-read it downstream.
        ctx.Request.EnableBuffering();
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        byte[] body = ms.ToArray();
        ctx.Request.Body.Position = 0;

        string canonical = HmacSigner.CanonicalString(
            ctx.Request.Method,
            ctx.Request.Path.ToString(),
            proto, ts, nonce, body);

        string providedSig = sigVals.ToString();
        bool ok = false;
        foreach (var secret in _cfg.AllSecrets())
        {
            if (HmacSigner.Verify(secret, canonical, providedSig, ts, now)) { ok = true; break; }
        }

        if (!ok)
        {
            await Reject(ctx, "bad signature");
            return;
        }

        await _next(ctx);
    }

    private async Task Reject(HttpContext ctx, string reason)
    {
        _log.LogWarning("Nimbus auth reject {Path} from {Ip}: {Reason}",
            ctx.Request.Path, ctx.Connection.RemoteIpAddress, reason);
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync($"unauthorized: {reason}");
    }
}
