namespace Nimbus.Proxy;

internal sealed class ReloadCommand : IAdminCommand
{
    public string Name => "reload";
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Permission => "nimbus.command.reload";
    public string Summary => "hot-reload proxy config and plugins without restarting";
    public string Usage => """{"cmd":"reload"}""";

    public Task<object> ExecuteAsync(AdminContext ctx)
    {
        if (ctx.Reload == null)
            return Task.FromResult<object>(new { ok = false, reason = "reload not available" });

        var result = ctx.Reload();
        Log.Info($"config reloaded via admin socket: {result}");
        return Task.FromResult<object>(new { ok = true, message = result });
    }
}
