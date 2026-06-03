namespace Nimbus.Proxy;

internal sealed class KickCommand : IAdminCommand
{
    public string Name => "kick";
    public string Permission => "nimbus.command.kick";
    public string Summary => "force-close a session";
    public string Usage => "kick <id>";

    public Task<object> ExecuteAsync(AdminContext ctx)
    {
        long id = ctx.Request.GetProperty("id").GetInt64();
        if (!ctx.Proxy.Sessions.TryGetValue(id, out var s))
            return Task.FromResult<object>(new { ok = false, reason = "session not found" });
        s.Close();
        return Task.FromResult<object>(new { ok = true });
    }
}
