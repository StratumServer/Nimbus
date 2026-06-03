namespace Nimbus.Proxy;

internal sealed class DrainCommand : IAdminCommand
{
    public string Name => "drain";
    public string Permission => "nimbus.command.drain";
    public string Summary => "stop routing new sessions to a backend";
    public string Usage => "drain <serverId>";

    public Task<object> ExecuteAsync(AdminContext ctx)
    {
        string serverId = ctx.Request.TryGetProperty("serverId", out var sidEl) ? (sidEl.GetString() ?? "") : "";
        if (string.IsNullOrEmpty(serverId))
            return Task.FromResult<object>(new { ok = false, reason = "missing 'serverId'" });
        bool added = ctx.Proxy.Router.Drain(serverId);
        return Task.FromResult<object>(new { ok = true, serverId, added });
    }
}

internal sealed class UndrainCommand : IAdminCommand
{
    public string Name => "undrain";
    public string Permission => "nimbus.command.undrain";
    public string Summary => "resume routing new sessions to a backend";
    public string Usage => "undrain <serverId>";

    public Task<object> ExecuteAsync(AdminContext ctx)
    {
        string serverId = ctx.Request.TryGetProperty("serverId", out var sidEl) ? (sidEl.GetString() ?? "") : "";
        if (string.IsNullOrEmpty(serverId))
            return Task.FromResult<object>(new { ok = false, reason = "missing 'serverId'" });
        bool removed = ctx.Proxy.Router.Undrain(serverId);
        return Task.FromResult<object>(new { ok = true, serverId, removed });
    }
}
