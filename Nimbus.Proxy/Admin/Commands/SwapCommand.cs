using System.Net.Sockets;

namespace Nimbus.Proxy;

internal sealed class SwapCommand : IAdminCommand
{
    public string Name => "swap";
    public IReadOnlyList<string> Aliases => new[] { "send", "transfer" };
    public string Permission => "nimbus.command.swap";
    public string Summary => "transfer a session to another backend";
    public string Usage => "swap <id> --server <serverId> [--redirect|--seamless]";

    public async Task<object> ExecuteAsync(AdminContext ctx)
    {
        var req = ctx.Request;
        if (!req.TryInt64("id", out var id))
            return AdminCommandError.Invalid(this, "id");

        string serverId = req.OptionalString("serverId") ?? "";
        string? reason = req.OptionalString("reason");

        string requestedMode = req.OptionalString("mode") ?? ctx.Proxy.Cfg.Transfers.DefaultMode;
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
            if (!req.TryString("host", out host) || !req.TryInt32("port", out port))
                return AdminCommandError.Usage(this, "need either serverId with the registry enabled, or host and port");
        }

        // Skip reservation work when the backend is already unreachable.
        if (!await TcpProbeAsync(host, port, TimeSpan.FromMilliseconds(1000)).ConfigureAwait(false))
            return new { ok = false, reason = $"target {host}:{port} unreachable (tcp probe)" };

        var target = new BackendEndpoint { Host = host, Port = port, ServerId = serverId };
        if (!string.Equals(mode, "seamless", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "redirect", StringComparison.OrdinalIgnoreCase))
            return AdminCommandError.Usage(this, $"unknown mode '{mode}'");

        var result = await session.RequestTransferAsync(target, mode, ctx.Proxy.Registry, reason, ctx.Proxy.RegistryCfg.FailOnError).ConfigureAwait(false);

        return result.failReason == null
            ? (object)new { ok = true, requestedMode = mode, mode = result.modeUsed, target = new { host, port, serverId } }
            : new { ok = false, requestedMode = mode, mode = result.modeUsed, reason = result.failReason };
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
