namespace Nimbus.Proxy;

internal sealed class StickyCommand : IAdminCommand
{
    public string Name => "sticky";
    public string Permission => "nimbus.command.sticky";
    public string Summary => "list staged sticky reconnect routes";
    public string Usage => "sticky";

    public Task<object> ExecuteAsync(AdminContext ctx)
    {
        var now = DateTime.UtcNow;
        var entries = ctx.Proxy.Stickies.Snapshot()
            .Select(e => new
            {
                uid = e.Uid,
                host = e.Target.Host,
                port = e.Target.Port,
                serverId = e.Target.ServerId,
                ttlSeconds = (int)Math.Max(0, (e.ExpiresAtUtc - now).TotalSeconds),
                reason = e.Reason,
            })
            .ToArray();
        return Task.FromResult<object>(new { ok = true, entries });
    }
}
