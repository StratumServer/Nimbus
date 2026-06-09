namespace Nimbus.Proxy;

internal sealed class AdminCommandRegistry
{
    private readonly Dictionary<string, IAdminCommand> commands;
    private readonly IAdminCommand[] handlers;

    public AdminCommandRegistry(IEnumerable<IAdminCommand> handlers)
    {
        this.handlers = handlers.ToArray();
        commands = new Dictionary<string, IAdminCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in this.handlers)
        {
            AddName(handler.Name, handler);
            foreach (var alias in handler.Aliases)
                AddName(alias, handler);
        }
    }

    public bool TryGet(string name, out IAdminCommand cmd) => commands.TryGetValue(name, out cmd!);
    public IReadOnlyCollection<IAdminCommand> Commands => handlers;

    private void AddName(string name, IAdminCommand handler)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException($"admin command {handler.GetType().Name} has an empty name or alias");
        if (commands.TryGetValue(name, out var existing))
            throw new InvalidOperationException($"admin command name '{name}' is used by {existing.Name} and {handler.Name}");
        commands.Add(name, handler);
    }

    public static AdminCommandRegistry Default() => new(new IAdminCommand[]
    {
        new HelpCommand(),
        new PingCommand(),
        new ListCommand(),
        new StatusCommand(),
        new PluginsCommand(),
        new KickCommand(),
        new ServersCommand(),
        new SwapCommand(),
        new StickyCommand(),
        new RouteCommand(),
        new DrainCommand(),
        new UndrainCommand(),
    });
}
