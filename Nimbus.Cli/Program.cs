using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Nimbus.Cli;

// CLI for the Nimbus.Proxy admin endpoint. One TCP connection per command.
// Defaults: host=127.0.0.1, port=42499. Override with --host/--port, NIMCTL_HOST/NIMCTL_PORT,
// or a nimctl.json file in CWD or next to the exe.
internal static class Program
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 42499;

    private static int Main(string[] args)
    {
        var (host, port, secret, rest) = ParseGlobalOptions(args);

        if (rest.Count == 0 || IsHelp(rest[0]))
        {
            PrintHelp();
            return rest.Count == 0 ? 2 : 0;
        }

        try
        {
            object payload = BuildPayload(rest);

            string response = SendAsync(host, port, secret, payload).GetAwaiter().GetResult();
            Console.WriteLine(PrettyPrint(response));
            return ExitCodeFromResponse(response);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"nimctl: {ex.Message}");
            return 1;
        }
    }

    // The admin frame a command line turns into, before it reaches a socket. Kept apart from
    // Main so the mapping can be read, and tested, without a proxy on the other end.
    internal static object BuildPayload(List<string> args)
    {
        string cmd = NormalizeCommand(args[0]);
        return cmd switch
        {
            "ping"    => new { cmd = "ping" },
            "help"    => new { cmd = "help" },
            "list"    => new { cmd = "list" },
            "status"  => BuildStatus(args),
            "plugins" => new { cmd = "plugins" },
            "kick"    => BuildKick(args),
            "servers" => BuildServers(args),
            "swap"    => BuildSwap(args),
            "sticky"  => new { cmd = "sticky" },
            "route"   => new { cmd = "route" },
            "drain"   => BuildDrain(args, "drain"),
            "undrain" => BuildDrain(args, "undrain"),
            "ban"     => BuildBan(args),
            "unban"   => BuildUnban(args),
            "bans"    => new { cmd = "bans" },
            "whitelist" => BuildWhitelist(args),
            "reload"  => new { cmd = "reload" },
            "raw"     => BuildRaw(args),
            _ => throw new ArgumentException($"unknown command: {cmd}"),
        };
    }

    // The exact line body that goes on the wire. A payload built by `raw` is already a parsed
    // JSON document and is passed through verbatim rather than reserialized.
    internal static string Serialize(object payload)
        => payload is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(payload);

    private static object BuildStatus(List<string> args)
    {
        long id = RequiredLong(args, 1, "<id>");
        return new { cmd = "status", id };
    }

    private static object BuildKick(List<string> args)
    {
        long id = RequiredLong(args, 1, "<id>");
        return new { cmd = "kick", id };
    }

    private static object BuildServers(List<string> args)
    {
        bool refresh = args.Contains("--refresh");
        return new { cmd = "servers", refresh };
    }

    private static object BuildSwap(List<string> args)
    {
        long id = RequiredLong(args, 1, "<id>");
        string? serverId = GetOpt(args, "--server") ?? GetOpt(args, "--serverId");
        string? host = GetOpt(args, "--host");
        string? portStr = GetOpt(args, "--port");
        string? reason = GetOpt(args, "--reason");
        bool seamless = args.Contains("--seamless") || args.Contains("--splice");
        bool redirect = args.Contains("--redirect");
        if (seamless && redirect) throw new ArgumentException("--seamless and --redirect are mutually exclusive");

        if (string.IsNullOrEmpty(serverId) && (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(portStr)))
            throw new ArgumentException("swap requires either --server <id> or both --host and --port");

        var d = new Dictionary<string, object?> { ["cmd"] = "swap", ["id"] = id };
        if (!string.IsNullOrEmpty(serverId)) d["serverId"] = serverId;
        if (!string.IsNullOrEmpty(host))     d["host"] = host;
        if (!string.IsNullOrEmpty(portStr))  d["port"] = int.Parse(portStr);
        if (!string.IsNullOrEmpty(reason))   d["reason"] = reason;
        if (seamless) d["mode"] = "seamless";
        if (redirect) d["mode"] = "redirect";
        return d;
    }

    // The admin socket has had ban, unban and bans since network bans landed, but nimctl
    // never grew the verbs, so the documented invocations only worked through `nimctl raw`.
    private static object BuildBan(List<string> args)
    {
        string? uid = GetOpt(args, "--uid");
        string? player = GetOpt(args, "--player") ?? GetOpt(args, "--name");
        if (string.IsNullOrEmpty(uid) && string.IsNullOrEmpty(player))
            throw new ArgumentException("ban requires --uid <uid> or --player <name>");

        string? serverId = GetOpt(args, "--server") ?? GetOpt(args, "--serverId");
        string? reason = GetOpt(args, "--reason");
        string? durationStr = GetOpt(args, "--duration");

        var d = new Dictionary<string, object?> { ["cmd"] = "ban" };
        if (!string.IsNullOrEmpty(uid))      d["uid"] = uid;
        if (!string.IsNullOrEmpty(player))   d["player"] = player;
        if (!string.IsNullOrEmpty(serverId)) d["serverId"] = serverId;
        if (!string.IsNullOrEmpty(reason))   d["reason"] = reason;
        if (!string.IsNullOrEmpty(durationStr))
        {
            if (!int.TryParse(durationStr, out int duration))
                throw new ArgumentException("--duration takes a number of seconds");
            d["duration"] = duration;
        }
        return d;
    }

    private static object BuildUnban(List<string> args)
    {
        string? uid = GetOpt(args, "--uid") ?? (args.Count >= 2 && !args[1].StartsWith("-") ? args[1] : null);
        if (string.IsNullOrEmpty(uid)) throw new ArgumentException("unban requires --uid <uid>");

        string? serverId = GetOpt(args, "--server") ?? GetOpt(args, "--serverId");
        var d = new Dictionary<string, object?> { ["cmd"] = "unban", ["uid"] = uid };
        if (!string.IsNullOrEmpty(serverId)) d["serverId"] = serverId;
        return d;
    }

    // `whitelist` takes a sub-verb rather than three top-level names, because add/remove/list all
    // read the same list and reading `whitelist list` out loud is what an operator expects. Bare
    // `whitelist` lists, which is the harmless one.
    private static object BuildWhitelist(List<string> args)
    {
        string sub = args.Count >= 2 && !args[1].StartsWith("-") ? args[1].ToLowerInvariant() : "list";
        switch (sub)
        {
            case "list" or "ls":
                return new { cmd = "whitelist-list" };

            case "add":
            {
                string? uid = GetOpt(args, "--uid");
                string? player = GetOpt(args, "--player") ?? GetOpt(args, "--name");
                if (string.IsNullOrEmpty(uid) && string.IsNullOrEmpty(player))
                    throw new ArgumentException("whitelist add requires --uid <uid> or --player <name>");

                var d = new Dictionary<string, object?> { ["cmd"] = "whitelist-add" };
                if (!string.IsNullOrEmpty(uid))    d["uid"] = uid;
                if (!string.IsNullOrEmpty(player)) d["player"] = player;

                string? serverId = GetOpt(args, "--server") ?? GetOpt(args, "--serverId");
                if (!string.IsNullOrEmpty(serverId)) d["serverId"] = serverId;
                string? note = GetOpt(args, "--note") ?? GetOpt(args, "--reason");
                if (!string.IsNullOrEmpty(note)) d["note"] = note;

                string? durationStr = GetOpt(args, "--duration");
                if (!string.IsNullOrEmpty(durationStr))
                {
                    if (!int.TryParse(durationStr, out int duration))
                        throw new ArgumentException("--duration takes a number of seconds");
                    d["duration"] = duration;
                }
                return d;
            }

            case "remove" or "rm" or "del":
            {
                string? uid = GetOpt(args, "--uid") ?? (args.Count >= 3 && !args[2].StartsWith("-") ? args[2] : null);
                if (string.IsNullOrEmpty(uid)) throw new ArgumentException("whitelist remove requires --uid <uid>");

                var d = new Dictionary<string, object?> { ["cmd"] = "whitelist-remove", ["uid"] = uid };
                string? serverId = GetOpt(args, "--server") ?? GetOpt(args, "--serverId");
                if (!string.IsNullOrEmpty(serverId)) d["serverId"] = serverId;
                return d;
            }

            default:
                throw new ArgumentException($"unknown whitelist sub-command: {sub} (add, remove, list)");
        }
    }

    private static object BuildDrain(List<string> args, string cmd)
    {
        string? serverId = args.Count >= 2 && !args[1].StartsWith("-") ? args[1] : (GetOpt(args, "--server") ?? GetOpt(args, "--serverId"));
        if (string.IsNullOrEmpty(serverId)) throw new ArgumentException($"{cmd} requires <serverId> or --server <id>");
        return new { cmd, serverId };
    }

    // Send arbitrary JSON straight to the admin endpoint.
    private static object BuildRaw(List<string> args)
    {
        if (args.Count < 2) throw new ArgumentException("raw requires a JSON argument");
        // Validate by re-parsing.
        using var doc = JsonDocument.Parse(args[1]);
        return JsonSerializer.Deserialize<JsonElement>(args[1]);
    }

    private static async Task<string> SendAsync(string host, int port, string? secret, object payload)
    {
        using var tcp = new TcpClient { NoDelay = true };
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await tcp.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);
        var stream = tcp.GetStream();

        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Only send auth when the proxy expects it.
        if (!string.IsNullOrEmpty(secret))
        {
            var authJson = JsonSerializer.Serialize(new { cmd = "auth", secret });
            var authBytes = Encoding.UTF8.GetBytes(authJson + "\n");
            await stream.WriteAsync(authBytes).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            var authResp = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(authResp) || !authResp.Contains("\"ok\":true", StringComparison.Ordinal))
                throw new InvalidOperationException($"auth failed: {authResp ?? "(no response)"}");
        }

        var bytes = Encoding.UTF8.GetBytes(Serialize(payload) + "\n");
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);

        var line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
        return line ?? "";
    }

    internal static (string host, int port, string? secret, List<string> rest) ParseGlobalOptions(string[] args)
    {
        string host = Environment.GetEnvironmentVariable("NIMCTL_HOST") ?? DefaultHost;
        int port = int.TryParse(Environment.GetEnvironmentVariable("NIMCTL_PORT"), out var ep) ? ep : DefaultPort;
        string? secret = Environment.GetEnvironmentVariable("NIMCTL_SECRET");

        (host, port, secret) = ApplyConfigFile(
            new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }, host, port, secret);

        var rest = new List<string>(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--host" && i + 1 < args.Length) { host = args[++i]; continue; }
            if (args[i] == "--port" && i + 1 < args.Length) { port = int.Parse(args[++i]); continue; }
            if (args[i] == "--secret" && i + 1 < args.Length) { secret = args[++i]; continue; }
            rest.Add(args[i]);
        }
        return (host, port, secret, rest);
    }

    // nimctl.json from the first of `dirs` that has one. { "Host": "...", "Port": ..., "Secret": "..." }
    // Only the first file found is read, and a malformed one is ignored rather than fatal: a
    // stray config in a working directory must not stop the CLI from running.
    internal static (string Host, int Port, string? Secret) ApplyConfigFile(
        IEnumerable<string> dirs, string host, int port, string? secret)
    {
        foreach (var dir in dirs)
        {
            var path = Path.Combine(dir, "nimctl.json");
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("Host", out var h) && h.ValueKind == JsonValueKind.String) host = h.GetString()!;
                if (doc.RootElement.TryGetProperty("Port", out var p) && p.ValueKind == JsonValueKind.Number) port = p.GetInt32();
                if (doc.RootElement.TryGetProperty("Secret", out var s) && s.ValueKind == JsonValueKind.String) secret = s.GetString();
            }
            catch { /* ignore malformed config */ }
            break;
        }
        return (host, port, secret);
    }

    internal static bool IsHelp(string s) => s is "-h" or "--help" or "help";

    internal static string NormalizeCommand(string cmd) => cmd switch
    {
        "?" => "help",
        "ls" or "players" => "list",
        "inspect" => "status",
        "plugin" => "plugins",
        "drop" => "kick",
        "serverlist" => "servers",
        "send" or "transfer" => "swap",
        "stickies" => "sticky",
        "routes" => "route",
        "resume" => "undrain",
        "wl" => "whitelist",
        _ => cmd,
    };

    private static long RequiredLong(List<string> args, int idx, string label)
    {
        if (args.Count <= idx) throw new ArgumentException($"missing {label}");
        if (!long.TryParse(args[idx], out var v)) throw new ArgumentException($"invalid {label}: {args[idx]}");
        return v;
    }

    private static string? GetOpt(List<string> args, string name)
    {
        for (int i = 0; i < args.Count - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    internal static string PrettyPrint(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "(no response)";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch { return raw; }
    }

    internal static int ExitCodeFromResponse(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                return 3;
            return 0;
        }
        catch { return 0; }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("nimctl - Nimbus.Proxy admin client");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  nimctl [--host H] [--port P] <command> [args]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  ping                                    health check");
        Console.WriteLine("  help                                    list admin commands and permissions");
        Console.WriteLine("  list                                    list active sessions");
        Console.WriteLine("  status <id>                             session detail (uid, phase, captured ident)");
        Console.WriteLine("  plugins                                 list loaded plugins");
        Console.WriteLine("  kick <id>                               force-close a session");
        Console.WriteLine("  servers [--refresh]                     dump registry snapshot");
        Console.WriteLine("  swap <id> --server <serverId> [--reason \"...\"] [--redirect|--seamless]");
        Console.WriteLine("  swap <id> --host <h> --port <p> [--reason \"...\"] [--redirect|--seamless]");
        Console.WriteLine("      default mode is --redirect (forge Packet_ServerRedirect, client reconnects).");
        Console.WriteLine("      --seamless   : optional Nimbus mod path. Falls back to redirect unless capable.");
        Console.WriteLine("                     --splice is accepted as a deprecated alias.");
        Console.WriteLine("  sticky                                  list staged sticky reconnect routes");
        Console.WriteLine("  route                                   show backend pool + health + drain state");
        Console.WriteLine("  drain <serverId>                        stop routing new sessions to <serverId>");
        Console.WriteLine("  undrain <serverId>                      resume routing new sessions to <serverId>");
        Console.WriteLine("  ban (--uid <uid> | --player <name>) [--server <id>] [--duration <s>] [--reason \"...\"]");
        Console.WriteLine("      no --server bans across the whole network; --duration 0 or omitted is permanent.");
        Console.WriteLine("  unban <uid> [--server <id>]             lift a ban");
        Console.WriteLine("  bans                                    list active bans");
        Console.WriteLine("  whitelist add (--uid <uid> | --player <name>) [--server <id>] [--duration <s>] [--note \"...\"]");
        Console.WriteLine("      no --server covers the whole network; --duration 0 or omitted is permanent.");
        Console.WriteLine("  whitelist remove <uid> [--server <id>]  drop an entry, disconnecting whoever loses access");
        Console.WriteLine("  whitelist list                          list entries and where they are enforced");
        Console.WriteLine("      enforcement is [whitelist] in nimbus.proxy.toml, never the list being non-empty.");
        Console.WriteLine("  reload                                  reload nimbus.proxy.toml and all plugins");
        Console.WriteLine("  raw '<json>'                            send a raw JSON line (for new commands)");
        Console.WriteLine();
        Console.WriteLine("Defaults: host=127.0.0.1 port=42499.");
        Console.WriteLine();
        Console.WriteLine("Auth: pass --secret <s>, set NIMCTL_SECRET, or add \"Secret\" to nimctl.json when the");
        Console.WriteLine("      proxy is configured with admin.secret.");
        Console.WriteLine("Overrides (highest wins): CLI flags > nimctl.json > NIMCTL_HOST/PORT env > built-in.");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0=ok, 1=error, 2=usage, 3=server replied ok:false.");
    }
}
