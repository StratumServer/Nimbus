using System.Text.Json;

namespace Nimbus.Proxy;

// Per-request bundle passed to admin command handlers. Owns no resources,,
// everything is a reference to the singleton proxy/config plus the parsed request payload.
internal sealed class AdminContext
{
    public ProxyListener Proxy { get; }
    public ProxyConfig Cfg { get; }
    public JsonElement Request { get; }
    public CancellationToken StopToken { get; }
    public AdminPermissions Permissions { get; }
    public IReadOnlyCollection<IAdminCommand> Commands { get; }

    public AdminContext(ProxyListener proxy, ProxyConfig cfg, JsonElement request,
        CancellationToken stopToken, AdminPermissions permissions, IReadOnlyCollection<IAdminCommand> commands)
    {
        Proxy = proxy;
        Cfg = cfg;
        Request = request;
        StopToken = stopToken;
        Permissions = permissions;
        Commands = commands;
    }
}

internal interface IAdminCommand
{
    string Name { get; }
    string Permission { get; }
    string Summary { get; }
    string Usage { get; }
    Task<object> ExecuteAsync(AdminContext ctx);
}

internal sealed class AdminPermissions
{
    private readonly HashSet<string> granted;

    public AdminPermissions(IEnumerable<string> granted)
    {
        this.granted = new HashSet<string>(granted.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
    }

    public bool Allows(string permission)
    {
        if (granted.Contains("*")) return true;
        if (string.IsNullOrWhiteSpace(permission)) return true;
        if (granted.Contains(permission)) return true;

        int dot = permission.Length;
        while ((dot = permission.LastIndexOf('.', dot - 1)) > 0)
        {
            if (granted.Contains(permission.Substring(0, dot) + ".*"))
                return true;
        }
        return false;
    }
}
