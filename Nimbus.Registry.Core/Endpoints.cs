using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nimbus.Registry.Services;
using Nimbus.Shared;
using Nimbus.Shared.Models;

namespace Nimbus.Registry;

public static class Endpoints
{
    public static void Map(WebApplication app)
    {
        // Health (unauthenticated, outside /api).
        app.MapGet("/", () => Results.Text(
            $"Nimbus registry. protocol={NimbusProtocol.ProtocolVersion} version={NimbusProtocol.NimbusVersion}",
            "text/plain"));

        app.MapGet("/health", (TimeProvider clock) => Results.Ok(new { ok = true, ts = clock.GetUtcNow().ToUnixTimeSeconds() }));

        // Heartbeat.
        app.MapPost("/api/heartbeat", async (HttpContext ctx, BackendRegistry reg, RegistryConfig cfg, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("Heartbeat");
            BackendHeartbeat? hb;
            try { hb = await ctx.Request.ReadFromJsonAsync<BackendHeartbeat>(); }
            catch { return Results.BadRequest(new { error = "malformed body" }); }
            if (hb is null || string.IsNullOrEmpty(hb.ServerId))
                return Results.BadRequest(new { error = "missing ServerId" });

            reg.Upsert(hb);
            if (cfg.LogHeartbeats)
                log.LogInformation("heartbeat {Id} players={P}/{M} tps={Tps:F1} maint={M2}",
                    hb.ServerId, hb.Players, hb.MaxPlayers, hb.Tps, hb.Maintenance);

            return Results.Ok(new BackendHeartbeatResponse { Ok = true, NextHeartbeatSeconds = 5 });
        });

        // Network snapshot.
        app.MapGet("/api/servers", (BackendRegistry reg) => Results.Ok(reg.Snapshot()));

        // Reservations. The mint rules live in ReservationService because the proxy's embedded
        // mode reaches them without passing through here; this handler is the HTTP dress on
        // them (parse, map the refusal to a status code).
        app.MapPost("/api/reservations", async (HttpContext ctx, ReservationService reservations, RegistryConfig cfg) =>
        {
            ReservationRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<ReservationRequest>(); }
            catch { return Results.BadRequest(new { error = "malformed body" }); }

            if (req is null)
                return Results.BadRequest(new { error = "PlayerUid + TargetServerId required" });

            var result = reservations.Mint(new ReservationMintRequest
            {
                PlayerUid = req.PlayerUid,
                PlayerName = req.PlayerName,
                SourceServerId = req.SourceServerId,
                TargetServerId = req.TargetServerId,
                TtlSeconds = req.TtlSeconds,
                MaxTtlSeconds = cfg.MaxReservationTtlSeconds,
                Reason = req.Reason,
                RealRemoteIp = req.RealRemoteIp,
                RealRemotePort = req.RealRemotePort,
                ClientTransferId = req.ClientTransferId,
            });

            return result.Status switch
            {
                ReservationMintStatus.MissingSubject
                    => Results.BadRequest(new { error = "PlayerUid + TargetServerId required" }),
                ReservationMintStatus.UnknownTarget
                    => Results.NotFound(new ReservationResponse { Ok = false, Error = "target server not registered" }),
                ReservationMintStatus.Banned
                    => Results.Json(new ReservationResponse { Ok = false, Error = "player is banned from the target server" },
                        statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Ok(new ReservationResponse { Ok = true, Reservation = result.Reservation }),
            };
        });

        // Backend consumes a reservation during identification. Single-use.
        app.MapPost("/api/reservations/{id}/consume", (string id, HttpContext ctx, ReservationStore store) =>
        {
            string target = ctx.Request.Query["target"].ToString();
            if (string.IsNullOrEmpty(target))
                return Results.BadRequest(new { error = "?target=<serverId> required" });

            var r = store.Consume(id, target);
            if (r is null)
                return Results.NotFound(new ReservationResponse { Ok = false, Error = "reservation invalid, expired, or target mismatch" });
            return Results.Ok(new ReservationResponse { Ok = true, Reservation = r });
        });

        // Target backend consumes by (uid, target) at identification time. Vanilla clients
        // can't carry a reservation id through the redirect, so we look up by uid.
        app.MapPost("/api/reservations/consume-by-uid", (HttpContext ctx, ReservationStore store) =>
        {
            string uid = ctx.Request.Query["uid"].ToString();
            string target = ctx.Request.Query["target"].ToString();
            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(target))
                return Results.BadRequest(new { error = "?uid=&target= required" });

            var r = store.ConsumeByUid(uid, target);
            if (r is null)
                return Results.NotFound(new ReservationResponse { Ok = false, Error = "no matching reservation" });
            return Results.Ok(new ReservationResponse { Ok = true, Reservation = r });
        });

        // Network bans. Held here so one ban covers every backend instead of being repeated
        // per savegame. Scoped bans (ServerId set) block a single backend.
        app.MapPost("/api/bans", async (HttpContext ctx, BanStore bans, TimeProvider clock) =>
        {
            BanRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<BanRequest>(); }
            catch { return Results.BadRequest(new { error = "malformed body" }); }

            var stamped = RegistryStamps.NewBan(req, clock.GetUtcNow().ToUnixTimeSeconds());
            if (stamped is null)
                return Results.BadRequest(new BanResponse { Ok = false, Error = "PlayerUid required" });

            return Results.Ok(new BanResponse { Ok = true, Ban = bans.Add(stamped) });
        });

        // Lift a ban. Omit ServerId to lift the network-wide one; a scoped ban must be lifted
        // with the same serverId it was created with.
        app.MapPost("/api/bans/lift", async (HttpContext ctx, BanStore bans) =>
        {
            BanLiftRequest? req;
            try { req = await ReadOptionalBodyAsync<BanLiftRequest>(ctx); }
            catch { return Results.BadRequest(new { error = "malformed body" }); }

            // The signature covers the body and not the query, so a body naming a player settles
            // both arguments and the query is never consulted. Deprecated: ?uid=/?server= answer
            // only when the body names nobody, which keeps a proxy older than this endpoint
            // working. Drop the fallback when NimbusProtocol.ProtocolVersion moves past 1, since
            // that is the point at which a mismatched proxy is refused by HmacAuthMiddleware
            // anyway.
            bool fromBody = !string.IsNullOrEmpty(req?.PlayerUid);
            string uid = fromBody ? req!.PlayerUid : ctx.Request.Query["uid"].ToString();
            if (string.IsNullOrEmpty(uid))
                return Results.BadRequest(new { error = "PlayerUid required" });

            string serverId = fromBody ? req!.ServerId : ctx.Request.Query["server"].ToString();
            bool lifted = bans.Lift(uid, serverId);
            if (!lifted)
                return Results.NotFound(new BanResponse { Ok = false, Error = "no matching ban" });
            return Results.Ok(new BanResponse { Ok = true });
        });

        app.MapGet("/api/bans", (BanStore bans) => Results.Ok(new BanListResponse { Ok = true, Bans = bans.Active() }));

        // Network whitelist. Same storage shape as the bans above, read the other way round:
        // an entry says a player may come in. Whether that is required at all is a proxy-side
        // toggle, the [whitelist] section of nimbus.proxy.toml, so nothing here refuses anything.
        app.MapPost("/api/whitelist", async (HttpContext ctx, WhitelistStore whitelist, TimeProvider clock) =>
        {
            WhitelistRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<WhitelistRequest>(); }
            catch { return Results.BadRequest(new { error = "malformed body" }); }

            var stamped = RegistryStamps.NewWhitelistEntry(req, clock.GetUtcNow().ToUnixTimeSeconds());
            if (stamped is null)
                return Results.BadRequest(new WhitelistResponse { Ok = false, Error = "PlayerUid required" });

            return Results.Ok(new WhitelistResponse { Ok = true, Entry = whitelist.Add(stamped) });
        });

        // Drop an entry. Omit ServerId to drop the network-wide one; a scoped entry must be
        // removed with the same serverId it was created with. Same body-over-query rule as
        // /api/bans/lift, for the same reason.
        app.MapPost("/api/whitelist/remove", async (HttpContext ctx, WhitelistStore whitelist) =>
        {
            WhitelistRemoveRequest? req;
            try { req = await ReadOptionalBodyAsync<WhitelistRemoveRequest>(ctx); }
            catch { return Results.BadRequest(new { error = "malformed body" }); }

            bool fromBody = !string.IsNullOrEmpty(req?.PlayerUid);
            string uid = fromBody ? req!.PlayerUid : ctx.Request.Query["uid"].ToString();
            if (string.IsNullOrEmpty(uid))
                return Results.BadRequest(new { error = "PlayerUid required" });

            string serverId = fromBody ? req!.ServerId : ctx.Request.Query["server"].ToString();
            if (!whitelist.Remove(uid, serverId))
                return Results.NotFound(new WhitelistResponse { Ok = false, Error = "no matching entry" });
            return Results.Ok(new WhitelistResponse { Ok = true });
        });

        app.MapGet("/api/whitelist", (WhitelistStore whitelist)
            => Results.Ok(new WhitelistListResponse { Ok = true, Entries = whitelist.Active() }));

        // Backends post here when someone asks the proxy to move a player.
        // The proxy drains the queue and runs its normal swap path.
        app.MapPost("/api/transfer-intents", async (HttpContext ctx, TransferIntentStore store, BackendRegistry reg) =>
        {
            TransferIntentRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<TransferIntentRequest>(); }
            catch { return Results.BadRequest(new { error = "malformed body" }); }

            if (req is null || string.IsNullOrEmpty(req.PlayerUid) || string.IsNullOrEmpty(req.TargetServerId))
                return Results.BadRequest(new TransferIntentResponse { Ok = false, Error = "PlayerUid + TargetServerId required" });

            var target = reg.Get(req.TargetServerId);
            if (target is null)
                return Results.NotFound(new TransferIntentResponse { Ok = false, Error = "target server not registered" });

            var intent = store.Add(req);
            return Results.Ok(new TransferIntentResponse { Ok = true, Intent = intent });
        });

        // Proxy polls this. Destructive drain (each intent delivered at most once).
        app.MapPost("/api/transfer-intents/drain", (TransferIntentStore store) =>
        {
            var taken = store.Drain();
            return Results.Ok(new TransferIntentDrainResponse { Ok = true, Intents = taken });
        });
    }

    // Reads a body that a caller is allowed to leave out entirely. An absent body is not an
    // error here, unlike the endpoints that require one: it means the caller is old enough to
    // still be passing its arguments in the query.
    private static async Task<T?> ReadOptionalBodyAsync<T>(HttpContext ctx) where T : class
    {
        if (ctx.Request.ContentLength is null or 0) return null;
        // RequestAborted, not None: finishing the read for a caller that has already hung up
        // buys nothing, and the handler above turns the cancellation into the same 400 a
        // truncated body gets.
        return await ctx.Request.ReadFromJsonAsync<T>(ctx.RequestAborted);
    }
}
