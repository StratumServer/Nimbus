using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

// Network whitelist, held by the registry so one entry covers every proxy. Shaped like the ban
// commands: `player` resolves against live sessions because operators know names, `uid` lists
// someone who is not currently connected.
//
// Adding an entry never turns enforcement on. That switch is [whitelist] in nimbus.proxy.toml,
// and it has to be, because the registry cannot know which backends a given proxy is gating.
internal sealed class WhitelistAddCommand : IAdminCommand
{
    public string Name => "whitelist-add";
    public string Permission => "nimbus.command.whitelist.add";
    public string Summary => "whitelist a player across the network, or on one backend";
    public string Usage => "whitelist-add (--uid <uid> | --player <name>) [--server <serverId>] [--duration <seconds>] [--note <text>]";

    public async Task<object> ExecuteAsync(AdminContext ctx)
    {
        if (ctx.Proxy.Registry == null)
            return new { ok = false, reason = "whitelists need a registry (registry.mode is 'disabled')" };

        var req = ctx.Request;
        string uid = req.OptionalString("uid") ?? "";
        string name = req.OptionalString("player") ?? "";
        ProxySession? online = null;

        if (string.IsNullOrEmpty(uid))
        {
            if (string.IsNullOrEmpty(name))
                return AdminCommandError.Usage(this, "need either uid or player");

            online = WhitelistLookup.ByName(ctx, name);
            if (online?.PlayerUid == null)
                return new { ok = false, reason = $"no live session for player '{name}'; whitelist by uid instead" };
            uid = online.PlayerUid!;
        }
        else
        {
            online = WhitelistLookup.ByUid(ctx, uid);
        }

        string serverId = req.OptionalString("serverId") ?? "";
        string note = req.OptionalString("note") ?? "";
        req.TryInt32("duration", out int duration);

        var request = new WhitelistRequest
        {
            PlayerUid = uid,
            PlayerName = !string.IsNullOrEmpty(name) ? name : online?.PlayerName ?? "",
            ServerId = serverId,
            Note = note,
            AddedBy = "admin",
            DurationSeconds = duration,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.StopToken);
        cts.CancelAfter(TimeSpan.FromSeconds(ctx.Proxy.RegistryCfg.HttpTimeoutSeconds + 1));
        var entry = await ctx.Proxy.Registry.AddWhitelistAsync(request, cts.Token).ConfigureAwait(false);
        if (entry == null)
            return new { ok = false, reason = "registry refused the whitelist entry" };

        // Apply immediately rather than waiting for the next refresh, so the player's next join
        // attempt sees the entry.
        try { await ctx.Proxy.Whitelist.RefreshAsync().ConfigureAwait(false); } catch { }

        return new
        {
            ok = true,
            uid = entry.PlayerUid,
            player = entry.PlayerName,
            scope = entry.IsNetworkWide ? "network" : entry.ServerId,
            expiresAtUnix = entry.ExpiresAtUnix,
            enforcing = ctx.Cfg.Whitelist.Enabled,
        };
    }
}

internal sealed class WhitelistRemoveCommand : IAdminCommand
{
    public string Name => "whitelist-remove";
    public string Permission => "nimbus.command.whitelist.remove";
    public string Summary => "drop a whitelist entry and disconnect whoever loses access";
    public string Usage => "whitelist-remove --uid <uid> [--server <serverId>]";

    public async Task<object> ExecuteAsync(AdminContext ctx)
    {
        if (ctx.Proxy.Registry == null)
            return new { ok = false, reason = "whitelists need a registry (registry.mode is 'disabled')" };

        if (!ctx.Request.TryString("uid", out var uid))
            return AdminCommandError.Missing(this, "uid");

        // Scoped entries are removed with the serverId they were created with; omitting it drops
        // the network-wide one.
        string serverId = ctx.Request.OptionalString("serverId") ?? "";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.StopToken);
        cts.CancelAfter(TimeSpan.FromSeconds(ctx.Proxy.RegistryCfg.HttpTimeoutSeconds + 1));
        bool removed = await ctx.Proxy.Registry.RemoveWhitelistAsync(uid, serverId, cts.Token).ConfigureAwait(false);
        if (!removed)
            return new { ok = false, uid, scope = string.IsNullOrEmpty(serverId) ? "network" : serverId };

        try { await ctx.Proxy.Whitelist.RefreshAsync().ConfigureAwait(false); } catch { }

        // Removing an entry can close a door the player is already standing behind. Which of
        // their sessions that is depends on the backend each one sits on and on what coverage is
        // left, so this walks the session table rather than reasoning from the removed entry.
        int kicked = 0;
        foreach (var kv in ctx.Proxy.Sessions)
        {
            var session = kv.Value;
            if (!string.Equals(session.PlayerUid, uid, StringComparison.OrdinalIgnoreCase)) continue;

            // A session with no backend yet reports a null serverId, which only whitelist.network
            // gates.
            string? current = ((IPlayer)session).CurrentServer?.ServerId;
            if (!ctx.Cfg.Whitelist.RequiresCoverage(current)) continue;
            if (ctx.Proxy.Whitelist.FindCovering(uid, current) != null) continue;

            ((IPlayer)session).Disconnect(ctx.Cfg.Whitelist.Network
                ? "This network is whitelisted."
                : "This server is whitelisted.");
            kicked++;
        }

        return new
        {
            ok = true,
            uid,
            scope = string.IsNullOrEmpty(serverId) ? "network" : serverId,
            kicked,
        };
    }
}

internal sealed class WhitelistListCommand : IAdminCommand
{
    public string Name => "whitelist-list";
    public IReadOnlyList<string> Aliases => new[] { "whitelist" };
    public string Permission => "nimbus.command.whitelist.list";
    public string Summary => "list active whitelist entries and where they are enforced";
    public string Usage => "whitelist-list";

    public async Task<object> ExecuteAsync(AdminContext ctx)
    {
        if (ctx.Proxy.Registry == null)
            return new { ok = false, reason = "whitelists need a registry (registry.mode is 'disabled')" };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.StopToken);
        cts.CancelAfter(TimeSpan.FromSeconds(ctx.Proxy.RegistryCfg.HttpTimeoutSeconds + 1));
        var entries = await ctx.Proxy.Registry.GetWhitelistAsync(cts.Token).ConfigureAwait(false);
        if (entries == null)
            return new { ok = false, reason = "registry unreachable" };

        return new
        {
            ok = true,
            count = entries.Count,
            // The list means nothing without the switches: an empty one with enforcement on is a
            // closed network, not an open one.
            network = ctx.Cfg.Whitelist.Network,
            servers = ctx.Cfg.Whitelist.Servers,
            synced = ctx.Proxy.Whitelist.HasSynced,
            entries = entries.ConvertAll(e => new
            {
                uid = e.PlayerUid,
                player = e.PlayerName,
                scope = e.IsNetworkWide ? "network" : e.ServerId,
                note = e.Note,
                addedBy = e.AddedBy,
                createdAtUnix = e.CreatedAtUnix,
                expiresAtUnix = e.ExpiresAtUnix,
            }),
        };
    }
}

internal static class WhitelistLookup
{
    public static ProxySession? ByName(AdminContext ctx, string name)
    {
        foreach (var kv in ctx.Proxy.Sessions)
            if (string.Equals(kv.Value.PlayerName, name, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return null;
    }

    public static ProxySession? ByUid(AdminContext ctx, string uid)
    {
        foreach (var kv in ctx.Proxy.Sessions)
            if (string.Equals(kv.Value.PlayerUid, uid, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return null;
    }
}
