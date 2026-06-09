namespace Nimbus.Proxy;

internal sealed class DrainCommand : IAdminCommand
{
    public string Name => "drain";
    public string Permission => "nimbus.command.drain";
    public string Summary => "stop routing new sessions to a backend";
    public string Usage => "drain <serverId>";

    public Task<object> ExecuteAsync(AdminContext ctx)
    {
        if (!ctx.Request.TryString("serverId", out var serverId))
            return Task.FromResult(AdminCommandError.Missing(this, "serverId"));
        bool added = ctx.Proxy.Router.Drain(serverId);
        return Task.FromResult<object>(new { ok = true, serverId, added });
    }
}

internal sealed class UndrainCommand : IAdminCommand
{
    public string Name => "undrain";
    public IReadOnlyList<string> Aliases => new[] { "resume" };
    public string Permission => "nimbus.command.undrain";
    public string Summary => "resume routing new sessions to a backend";
    public string Usage => "undrain <serverId>";

    public Task<object> ExecuteAsync(AdminContext ctx)
    {
        if (!ctx.Request.TryString("serverId", out var serverId))
            return Task.FromResult(AdminCommandError.Missing(this, "serverId"));
        bool removed = ctx.Proxy.Router.Undrain(serverId);
        return Task.FromResult<object>(new { ok = true, serverId, removed });
    }
}
