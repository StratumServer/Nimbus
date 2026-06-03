namespace Nimbus.Proxy;

internal sealed class ListCommand : IAdminCommand
{
    public string Name => "list";
    public string Permission => "nimbus.command.list";
    public string Summary => "list active sessions";
    public string Usage => "list";

    public Task<object> ExecuteAsync(AdminContext ctx)
    {
        var sessions = ctx.Proxy.Sessions.Values
            .Select(s => new { id = s.Id, phase = s.Phase.ToString(), player = s.PlayerName, uid = s.PlayerUid })
            .ToArray();
        return Task.FromResult<object>(new { sessions });
    }
}
