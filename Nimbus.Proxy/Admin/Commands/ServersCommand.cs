namespace Nimbus.Proxy;

internal sealed class ServersCommand : IAdminCommand
{
    public string Name => "servers";
    public string Permission => "nimbus.command.servers";
    public string Summary => "dump registry server snapshot";
    public string Usage => "servers [--refresh]";

    public async Task<object> ExecuteAsync(AdminContext ctx)
    {
        if (ctx.Proxy.Registry == null) return new { ok = false, reason = "registry disabled" };
        bool refresh = ctx.Request.TryGetProperty("refresh", out var rEl) && rEl.GetBoolean();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.StopToken);
        cts.CancelAfter(TimeSpan.FromSeconds(ctx.Proxy.RegistryCfg.HttpTimeoutSeconds + 1));
        var snap = await ctx.Proxy.Registry.GetServersAsync(cts.Token, refresh).ConfigureAwait(false);
        if (snap == null) return new { ok = false, reason = "registry unavailable" };
        return new { ok = true, snapshot = snap };
    }
}
