namespace Nimbus.Proxy;

internal sealed class PingCommand : IAdminCommand
{
    public string Name => "ping";
    public string Permission => "nimbus.command.ping";
    public string Summary => "health check";
    public string Usage => "ping";

    public Task<object> ExecuteAsync(AdminContext ctx)
        => Task.FromResult<object>(new { ok = true, version = "nimbus.proxy/0.1" });
}
