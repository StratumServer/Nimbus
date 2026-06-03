using System.Net.Sockets;

namespace Nimbus.Proxy;

internal sealed class SwapCommand : IAdminCommand
{
    public string Name => "swap";
    public string Permission => "nimbus.command.swap";
    public string Summary => "transfer a session to another backend";
    public string Usage => "swap <id> --server <serverId> [--redirect|--seamless]";

    public async Task<object> ExecuteAsync(AdminContext ctx)
    {
        var req = ctx.Request;
        long id = req.GetProperty("id").GetInt64();
        string serverId = req.TryGetProperty("serverId", out var sidEl) ? (sidEl.GetString() ?? "") : "";
        string? reason = req.TryGetProperty("reason", out var rEl) ? rEl.GetString() : null;

        // Keep "splice" working for older tools.
        string requestedMode = req.TryGetProperty("mode", out var mEl)
            ? (mEl.GetString() ?? ctx.Proxy.Cfg.Transfers.DefaultMode)
            : ctx.Proxy.Cfg.Transfers.DefaultMode;
        string mode = string.Equals(requestedMode, "splice", StringComparison.OrdinalIgnoreCase) ? "seamless" : requestedMode;

        if (!ctx.Proxy.Sessions.TryGetValue(id, out var session))
            return new { ok = false, reason = "session not found" };

        // Prefer named registry targets when the registry is enabled.
        string host;
        int port;
        if (!string.IsNullOrEmpty(serverId) && ctx.Proxy.Registry != null)
        {
            using var rcts = CancellationTokenSource.CreateLinkedTokenSource(ctx.StopToken);
            rcts.CancelAfter(TimeSpan.FromSeconds(ctx.Proxy.RegistryCfg.HttpTimeoutSeconds + 1));
            var b = await ctx.Proxy.Registry.ResolveByServerIdAsync(serverId, rcts.Token).ConfigureAwait(false);
            if (b == null) return new { ok = false, reason = $"unknown serverId '{serverId}' in registry" };
            if (b.Stale) return new { ok = false, reason = $"target '{serverId}' is stale (no recent heartbeat)" };
            if (b.Maintenance) return new { ok = false, reason = $"target '{serverId}' is in maintenance" };
            host = b.PublicHost;
            port = b.PublicPort;
        }
        else
        {
            if (!req.TryGetProperty("host", out var hEl) || !req.TryGetProperty("port", out var pEl))
                return new { ok = false, reason = "need either serverId (with Nimbus enabled) or host+port" };
            host = hEl.GetString() ?? "127.0.0.1";
            port = pEl.GetInt32();
        }

        // Skip reservation work when the backend is already unreachable.
        if (!await TcpProbeAsync(host, port, TimeSpan.FromMilliseconds(1000)).ConfigureAwait(false))
            return new { ok = false, reason = $"target {host}:{port} unreachable (tcp probe)" };

        var target = new BackendEndpoint { Host = host, Port = port, ServerId = serverId };
        string? failReason;
        if (string.Equals(mode, "seamless", StringComparison.OrdinalIgnoreCase))
        {
            if (!ctx.Proxy.Cfg.Transfers.AllowSeamless)
                return new { ok = false, mode, reason = "seamless transfers are disabled (set transfers.allow_seamless = true; requires the Nimbus client+server mod)" };
            failReason = await session.RequestSeamlessAsync(target, ctx.Proxy.Registry, reason, ctx.Proxy.RegistryCfg.FailOnError).ConfigureAwait(false);
        }
        else if (string.Equals(mode, "redirect", StringComparison.OrdinalIgnoreCase))
        {
            failReason = await session.RequestRedirectAsync(target, ctx.Proxy.Registry, reason, ctx.Proxy.RegistryCfg.FailOnError).ConfigureAwait(false);
        }
        else
        {
            return new { ok = false, reason = $"unknown mode '{mode}' (expected 'redirect' or 'seamless')" };
        }

        return failReason == null
            ? (object)new { ok = true, mode, target = new { host, port, serverId } }
            : new { ok = false, mode, reason = failReason };
    }

    private static async Task<bool> TcpProbeAsync(string host, int port, TimeSpan timeout)
    {
        using var tcp = new TcpClient { NoDelay = true };
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return tcp.Connected;
        }
        catch { return false; }
    }
}
