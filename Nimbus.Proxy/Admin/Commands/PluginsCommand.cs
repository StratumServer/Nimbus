namespace Nimbus.Proxy;

internal sealed class PluginsCommand : IAdminCommand
{
    public string Name => "plugins";
    public IReadOnlyList<string> Aliases => new[] { "plugin" };
    public string Permission => "nimbus.command.plugins";
    public string Summary => "list loaded plugins";
    public string Usage => "plugins";

    public Task<object> ExecuteAsync(AdminContext ctx)
    {
        var plugins = ctx.Plugins
            .OrderBy(p => p.Metadata.Id, StringComparer.OrdinalIgnoreCase)
            .Select(p => new
            {
                id = p.Metadata.Id,
                name = p.Metadata.Name,
                version = p.Metadata.Version,
                apiVersion = p.Metadata.ApiVersion,
                dependencies = p.Metadata.Dependencies,
                source = p.SourceFile,
            })
            .ToArray();

        return Task.FromResult<object>(new { ok = true, plugins });
    }
}
