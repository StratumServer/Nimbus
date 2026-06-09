using System.Text.Json;

namespace Nimbus.Proxy;

// Per-request bundle passed to admin command handlers.
internal sealed class AdminContext
{
    public ProxyListener Proxy { get; }
    public ProxyConfig Cfg { get; }
    public AdminRequest Request { get; }
    public CancellationToken StopToken { get; }
    public AdminPermissions Permissions { get; }
    public IReadOnlyCollection<IAdminCommand> Commands { get; }
    public IReadOnlyList<LoadedPlugin> Plugins { get; }

    public AdminContext(ProxyListener proxy, ProxyConfig cfg, JsonElement request,
        CancellationToken stopToken, AdminPermissions permissions, IReadOnlyCollection<IAdminCommand> commands,
        IReadOnlyList<LoadedPlugin> plugins)
    {
        Proxy = proxy;
        Cfg = cfg;
        Request = new AdminRequest(request);
        StopToken = stopToken;
        Permissions = permissions;
        Commands = commands;
        Plugins = plugins;
    }
}

internal readonly struct AdminRequest
{
    private readonly JsonElement root;

    public AdminRequest(JsonElement root)
    {
        this.root = root;
    }

    public bool TryString(string name, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    public string? OptionalString(string name)
        => TryString(name, out var value) ? value : null;

    public bool TryInt64(string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var el) &&
               el.ValueKind == JsonValueKind.Number &&
               el.TryGetInt64(out value);
    }

    public bool TryInt32(string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var el) &&
               el.ValueKind == JsonValueKind.Number &&
               el.TryGetInt32(out value);
    }

    public bool Bool(string name, bool fallback = false)
    {
        if (!root.TryGetProperty(name, out var el)) return fallback;
        return el.ValueKind == JsonValueKind.True || (el.ValueKind != JsonValueKind.False && fallback);
    }
}

internal interface IAdminCommand
{
    string Name { get; }
    IReadOnlyList<string> Aliases => Array.Empty<string>();
    string Permission { get; }
    string Summary { get; }
    string Usage { get; }
    Task<object> ExecuteAsync(AdminContext ctx);
}

internal static class AdminCommandError
{
    public static object Missing(IAdminCommand command, string name)
        => new { ok = false, reason = $"missing '{name}'", usage = command.Usage };

    public static object Invalid(IAdminCommand command, string name)
        => new { ok = false, reason = $"invalid '{name}'", usage = command.Usage };

    public static object Usage(IAdminCommand command, string reason)
        => new { ok = false, reason, usage = command.Usage };
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
