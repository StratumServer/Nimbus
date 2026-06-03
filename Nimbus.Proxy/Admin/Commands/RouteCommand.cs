using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

internal sealed class RouteCommand : IAdminCommand
{
    public string Name => "route";
    public string Permission => "nimbus.command.route";
    public string Summary => "show configured route pool and health";
    public string Usage => "route";

    public async Task<object> ExecuteAsync(AdminContext ctx)
    {
        var cand = ctx.Proxy.Router.Candidates.Select(c => new { c.ServerId, c.Host, c.Port }).ToArray();
        var drained = ctx.Proxy.Router.ListDrained();
        NetworkSnapshot? snap = null;
        if (ctx.Proxy.Registry != null)
        {
            using var rcts = CancellationTokenSource.CreateLinkedTokenSource(ctx.StopToken);
            rcts.CancelAfter(TimeSpan.FromSeconds(ctx.Proxy.RegistryCfg.HttpTimeoutSeconds + 1));
            snap = await ctx.Proxy.Registry.GetServersAsync(rcts.Token).ConfigureAwait(false);
        }
        var view = cand.Select(c =>
        {
            BackendSnapshot? b = null;
            if (snap != null && !string.IsNullOrEmpty(c.ServerId))
            {
                foreach (var x in snap.Backends)
                    if (string.Equals(x.ServerId, c.ServerId, StringComparison.OrdinalIgnoreCase)) { b = x; break; }
            }
            return new
            {
                serverId = c.ServerId,
                host = c.Host,
                port = c.Port,
                drained = !string.IsNullOrEmpty(c.ServerId) && ctx.Proxy.Router.IsDrained(c.ServerId),
                known = b != null,
                stale = b?.Stale ?? (bool?)null,
                maintenance = b?.Maintenance ?? (bool?)null,
                players = b?.Players ?? (int?)null,
                maxPlayers = b?.MaxPlayers ?? (int?)null,
            };
        }).ToArray();
        return new { ok = true, drained, candidates = view };
    }
}
