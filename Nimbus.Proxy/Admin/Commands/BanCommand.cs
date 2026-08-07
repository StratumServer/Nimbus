using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

// Network bans, held by the registry so one ban covers every backend. Operators normally know
// player names rather than uids, so `player` resolves against live sessions; `uid` bans someone
// who is not currently connected.
internal sealed class BanCommand : IAdminCommand
{
    public string Name => "ban";
    public string Permission => "nimbus.command.ban";
    public string Summary => "ban a player from the whole network, or from one backend";
    public string Usage => "ban (--uid <uid> | --player <name>) [--server <serverId>] [--duration <seconds>] [--reason <text>]";

    public async Task<object> ExecuteAsync(AdminContext ctx)
    {
        if (ctx.Proxy.Registry == null)
            return new { ok = false, reason = "bans need a registry (registry.mode is 'disabled')" };

        var req = ctx.Request;
        string uid = req.OptionalString("uid") ?? "";
        string name = req.OptionalString("player") ?? "";
        ProxySession? online = null;

        if (string.IsNullOrEmpty(uid))
        {
            if (string.IsNullOrEmpty(name))
                return AdminCommandError.Usage(this, "need either uid or player");

            online = FindByName(ctx, name);
            if (online?.PlayerUid == null)
                return new { ok = false, reason = $"no live session for player '{name}'; ban by uid instead" };
            uid = online.PlayerUid;
        }
        else
        {
            online = FindByUid(ctx, uid);
        }

        string serverId = req.OptionalString("serverId") ?? "";
        string reason = req.OptionalString("reason") ?? "";
        req.TryInt32("duration", out int duration);

        var request = new BanRequest
        {
            PlayerUid = uid,
            PlayerName = !string.IsNullOrEmpty(name) ? name : online?.PlayerName ?? "",
            ServerId = serverId,
            Reason = reason,
            BannedBy = "admin",
            DurationSeconds = duration,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.StopToken);
        cts.CancelAfter(TimeSpan.FromSeconds(ctx.Proxy.RegistryCfg.HttpTimeoutSeconds + 1));
        var ban = await ctx.Proxy.Registry.AddBanAsync(request, cts.Token).ConfigureAwait(false);
        if (ban == null)
            return new { ok = false, reason = "registry refused the ban" };

        // Apply immediately rather than waiting for the next refresh, so the kick below and any
        // reconnect attempt see the ban.
        try { await ctx.Proxy.Bans.RefreshAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            // The kick sweep reads the ban the registry just returned, not the cache, so it runs
            // either way. Only the reconnect gate leans on the cache, and it is caught up by the
            // next poll; until then the player could get back in, which the operator should hear.
            Log.Warn($"ban cache refresh failed after banning {uid}: {ex.GetType().Name}: {ex.Message}; the gate picks it up on the next poll");
        }

        // A ban should not leave the player sitting on a backend it covers. A network-wide ban
        // takes every session they hold; a scoped one only the sessions on that backend, which
        // is why this walks the session table instead of using the single lookup above.
        string kickScope = ban.IsNetworkWide ? "this network" : "this server";
        string kickReason = string.IsNullOrWhiteSpace(reason)
            ? $"You are banned from {kickScope}."
            : $"You are banned from {kickScope}: {reason}";

        int kicked = 0;
        foreach (var session in ctx.Proxy.Sessions.Values)
        {
            if (!string.Equals(session.PlayerUid, uid, StringComparison.OrdinalIgnoreCase)) continue;
            // A session with no backend yet reports a null serverId, which only a network-wide
            // ban blocks.
            if (!ban.Blocks(((IPlayer)session).CurrentServer?.ServerId)) continue;
            ((IPlayer)session).Disconnect(kickReason);
            kicked++;
        }

        return new
        {
            ok = true,
            uid = ban.PlayerUid,
            player = ban.PlayerName,
            scope = ban.IsNetworkWide ? "network" : ban.ServerId,
            expiresAtUnix = ban.ExpiresAtUnix,
            kicked,
        };
    }

    private static ProxySession? FindByName(AdminContext ctx, string name)
        => ctx.Proxy.Sessions.Values.FirstOrDefault(s => string.Equals(s.PlayerName, name, StringComparison.OrdinalIgnoreCase));

    private static ProxySession? FindByUid(AdminContext ctx, string uid)
        => ctx.Proxy.Sessions.Values.FirstOrDefault(s => string.Equals(s.PlayerUid, uid, StringComparison.OrdinalIgnoreCase));
}

internal sealed class UnbanCommand : IAdminCommand
{
    public string Name => "unban";
    public IReadOnlyList<string> Aliases => new[] { "pardon" };
    public string Permission => "nimbus.command.unban";
    public string Summary => "lift a network ban";
    public string Usage => "unban --uid <uid> [--server <serverId>]";

    public async Task<object> ExecuteAsync(AdminContext ctx)
    {
        if (ctx.Proxy.Registry == null)
            return new { ok = false, reason = "bans need a registry (registry.mode is 'disabled')" };

        if (!ctx.Request.TryString("uid", out var uid))
            return AdminCommandError.Missing(this, "uid");

        // Scoped bans are lifted with the serverId they were created with; omitting it lifts the
        // network-wide entry.
        string serverId = ctx.Request.OptionalString("serverId") ?? "";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.StopToken);
        cts.CancelAfter(TimeSpan.FromSeconds(ctx.Proxy.RegistryCfg.HttpTimeoutSeconds + 1));
        bool lifted = await ctx.Proxy.Registry.LiftBanAsync(uid, serverId, cts.Token).ConfigureAwait(false);

        if (lifted)
        {
            try { await ctx.Proxy.Bans.RefreshAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                // Nothing here reads the cache afterwards; the cost of the failure is that the
                // pardoned player keeps being refused until the next poll, which is the kind of
                // thing an operator wants told rather than discovered.
                Log.Warn($"ban cache refresh failed after lifting the ban on {uid}: {ex.GetType().Name}: {ex.Message}; the gate picks it up on the next poll");
            }
        }

        return new { ok = lifted, uid, scope = string.IsNullOrEmpty(serverId) ? "network" : serverId };
    }
}

internal sealed class BansCommand : IAdminCommand
{
    public string Name => "bans";
    public string Permission => "nimbus.command.bans";
    public string Summary => "list active network bans";
    public string Usage => "bans";

    public async Task<object> ExecuteAsync(AdminContext ctx)
    {
        if (ctx.Proxy.Registry == null)
            return new { ok = false, reason = "bans need a registry (registry.mode is 'disabled')" };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.StopToken);
        cts.CancelAfter(TimeSpan.FromSeconds(ctx.Proxy.RegistryCfg.HttpTimeoutSeconds + 1));
        var bans = await ctx.Proxy.Registry.GetBansAsync(cts.Token).ConfigureAwait(false);
        if (bans == null)
            return new { ok = false, reason = "registry unreachable" };

        return new
        {
            ok = true,
            count = bans.Count,
            bans = bans.ConvertAll(b => new
            {
                uid = b.PlayerUid,
                player = b.PlayerName,
                scope = b.IsNetworkWide ? "network" : b.ServerId,
                reason = b.Reason,
                bannedBy = b.BannedBy,
                createdAtUnix = b.CreatedAtUnix,
                expiresAtUnix = b.ExpiresAtUnix,
            }),
        };
    }
}
