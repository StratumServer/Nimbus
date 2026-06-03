namespace Nimbus.Proxy;

internal sealed class HelpCommand : IAdminCommand
{
    public string Name => "help";
    public string Permission => "nimbus.command.help";
    public string Summary => "list admin commands";
    public string Usage => "help";

    public Task<object> ExecuteAsync(AdminContext ctx)
    {
        var commands = ctx.Commands
            .Where(c => ctx.Permissions.Allows(c.Permission))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new
            {
                name = c.Name,
                permission = c.Permission,
                summary = c.Summary,
                usage = c.Usage,
            })
            .ToArray();

        return Task.FromResult<object>(new { ok = true, commands });
    }
}
