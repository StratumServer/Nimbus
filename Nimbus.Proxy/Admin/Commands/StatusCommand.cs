namespace Nimbus.Proxy;

internal sealed class StatusCommand : IAdminCommand
{
    public string Name => "status";
    public IReadOnlyList<string> Aliases => new[] { "inspect" };
    public string Permission => "nimbus.command.status";
    public string Summary => "show one session";
    public string Usage => "status <id>";

    public Task<object> ExecuteAsync(AdminContext ctx)
    {
        if (!ctx.Request.TryInt64("id", out var id))
            return Task.FromResult(AdminCommandError.Invalid(this, "id"));
        if (!ctx.Proxy.Sessions.TryGetValue(id, out var s))
            return Task.FromResult<object>(new { ok = false, reason = "session not found" });
        return Task.FromResult<object>(new
        {
            ok = true,
            id = s.Id,
            phase = s.Phase.ToString(),
            client = s.ClientRemote,
            player = s.PlayerName,
            uid = s.PlayerUid,
            identCaptured = s.HasIdentification
        });
    }
}
