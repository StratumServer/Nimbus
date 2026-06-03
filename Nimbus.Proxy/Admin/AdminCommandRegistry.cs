namespace Nimbus.Proxy;

// Looks up admin commands by name. Built once at proxy startup
internal sealed class AdminCommandRegistry
{
    private readonly Dictionary<string, IAdminCommand> commands;

    public AdminCommandRegistry(IEnumerable<IAdminCommand> handlers)
    {
        commands = handlers.ToDictionary(h => h.Name, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGet(string name, out IAdminCommand cmd) => commands.TryGetValue(name, out cmd!);
    public IReadOnlyCollection<IAdminCommand> Commands => commands.Values;

    public static AdminCommandRegistry Default() => new(new IAdminCommand[]
    {
        new HelpCommand(),
        new PingCommand(),
        new ListCommand(),
        new StatusCommand(),
        new KickCommand(),
        new ServersCommand(),
        new SwapCommand(),
        new StickyCommand(),
        new RouteCommand(),
        new DrainCommand(),
        new UndrainCommand(),
    });
}
