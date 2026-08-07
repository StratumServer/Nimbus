using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Nimbus.Cli;

// CLI for the Nimbus.Proxy admin endpoint. One TCP connection per command.
// Defaults: host=127.0.0.1, port=42499. Override with --host/--port before the command, with
// NIMCTL_HOST/NIMCTL_PORT, or with a nimctl.json file in CWD or next to the exe.
internal static class Program
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 42499;

    // Named because three places have to agree on it and only one of them looks like it does:
    // BuildPayload dispatches on it, NormalizeCommand maps the `evac` alias onto it, and
    // ReadTimeout gives it the long budget. A rename that missed the third would leave evacuate
    // timing out at fifteen seconds with nothing to say why.
    private const string EvacuateCommandName = "evacuate";

    // One instance rather than one per call: JsonSerializerOptions freezes itself on first use and
    // building a fresh one each time rebuilds the converter cache behind it.
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static int Main(string[] args)
    {
        // Parsing is inside the try because a bad --port is now a usage error with a message on
        // stderr rather than a FormatException with a stack trace behind it.
        try
        {
            var (host, port, secret, rest) = ParseGlobalOptions(args);

            if (rest.Count == 0 || IsHelp(rest[0]))
            {
                PrintHelp();
                return rest.Count == 0 ? 2 : 0;
            }

            RejectMisplacedConnectionFlags(rest);
            object payload = BuildPayload(rest);

            string response = SendAsync(host, port, secret, payload, ReadTimeout(rest)).GetAwaiter().GetResult();
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
            EvacuateCommandName => BuildEvacuate(args),
            "ban"     => BuildBan(args),
            "unban"   => BuildUnban(args),
            "bans"    => new { cmd = "bans" },
            "whitelist" => BuildWhitelist(args),
            "token"   => BuildToken(args),
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

    private static Dictionary<string, object?> BuildSwap(List<string> args)
    {
        long id = RequiredLong(args, 1, "<id>");
        string? serverId = ServerIdOpt(args);
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
        if (!string.IsNullOrEmpty(portStr))  d["port"] = ParsePort(portStr);
        if (!string.IsNullOrEmpty(reason))   d["reason"] = reason;
        if (seamless) d["mode"] = "seamless";
        if (redirect) d["mode"] = "redirect";
        return d;
    }

    // The admin socket has had ban, unban and bans since network bans landed, but nimctl
    // never grew the verbs, so the documented invocations only worked through `nimctl raw`.
    private static Dictionary<string, object?> BuildBan(List<string> args)
    {
        string? uid = GetOpt(args, "--uid");
        string? player = GetOpt(args, "--player") ?? GetOpt(args, "--name");
        if (string.IsNullOrEmpty(uid) && string.IsNullOrEmpty(player))
            throw new ArgumentException("ban requires --uid <uid> or --player <name>");

        string? serverId = ServerIdOpt(args);
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

    private static Dictionary<string, object?> BuildUnban(List<string> args)
    {
        string? uid = GetOpt(args, "--uid") ?? (args.Count >= 2 && !args[1].StartsWith('-') ? args[1] : null);
        if (string.IsNullOrEmpty(uid)) throw new ArgumentException("unban requires --uid <uid>");

        string? serverId = ServerIdOpt(args);
        var d = new Dictionary<string, object?> { ["cmd"] = "unban", ["uid"] = uid };
        if (!string.IsNullOrEmpty(serverId)) d["serverId"] = serverId;
        return d;
    }

    // `whitelist` takes a sub-verb rather than three top-level names, because add/remove/list all
    // read the same list and reading `whitelist list` out loud is what an operator expects. Bare
    // `whitelist` lists, which is the harmless one.
    //
    // CA1859 asks for Dictionary<string, object?> here as it does for the builders around it. It
    // does not compile: the `list` arm has no arguments to carry and returns the same anonymous
    // one-field object every no-argument verb in BuildPayload returns, so object is the only type
    // this signature can have.
#pragma warning disable CA1859
    private static object BuildWhitelist(List<string> args)
#pragma warning restore CA1859
    {
        string sub = args.Count >= 2 && !args[1].StartsWith('-') ? args[1].ToLowerInvariant() : "list";
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

                string? serverId = ServerIdOpt(args);
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
                string? uid = GetOpt(args, "--uid") ?? (args.Count >= 3 && !args[2].StartsWith('-') ? args[2] : null);
                if (string.IsNullOrEmpty(uid)) throw new ArgumentException("whitelist remove requires --uid <uid>");

                var d = new Dictionary<string, object?> { ["cmd"] = "whitelist-remove", ["uid"] = uid };
                string? serverId = ServerIdOpt(args);
                if (!string.IsNullOrEmpty(serverId)) d["serverId"] = serverId;
                return d;
            }

            default:
                throw new ArgumentException($"unknown whitelist sub-command: {sub} (add, remove, list)");
        }
    }

    // `token` takes a sub-verb for the same reason `whitelist` does: create, revoke and list all
    // read the one list. Bare `token` lists, which is the harmless one, and the only one that
    // never puts a secret on a terminal.
    private static object BuildToken(List<string> args)
    {
        string sub = args.Count >= 2 && !args[1].StartsWith('-') ? args[1].ToLowerInvariant() : "list";
        return sub switch
        {
            "list" or "ls" => new { cmd = "token-list" },
            "create" or "new" or "add" => BuildTokenCreate(args),
            "revoke" or "rm" or "del" => BuildTokenRevoke(args),
            _ => throw new ArgumentException($"unknown token sub-command: {sub} (create, revoke, list)"),
        };
    }

    private static Dictionary<string, object?> BuildTokenCreate(List<string> args)
    {
        string? name = GetOpt(args, "--name");
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("token create requires --name <name>");

        string? scopes = GetOpt(args, "--scopes") ?? GetOpt(args, "--scope");
        if (string.IsNullOrEmpty(scopes))
            throw new ArgumentException("token create requires --scopes <a,b> (bans:read, bans:write, whitelist:read, whitelist:write, servers:read)");

        var d = new Dictionary<string, object?> { ["cmd"] = "token-create", ["name"] = name, ["scopes"] = scopes };

        bool permanent = args.Contains("--permanent");
        string? durationStr = GetOpt(args, "--duration");
        // Refused rather than resolved in nimctl's favour: one of the two is being ignored
        // whichever way it is settled, and the operator is the only one who knows which they
        // meant.
        if (permanent && !string.IsNullOrEmpty(durationStr))
            throw new ArgumentException("--permanent and --duration are mutually exclusive");
        if (permanent) d["permanent"] = true;
        if (!string.IsNullOrEmpty(durationStr))
        {
            if (!int.TryParse(durationStr, out int duration))
                throw new ArgumentException("--duration takes a number of seconds");
            d["duration"] = duration;
        }
        return d;
    }

    private static object BuildTokenRevoke(List<string> args)
    {
        string? id = GetOpt(args, "--id") ?? (args.Count >= 3 && !args[2].StartsWith('-') ? args[2] : null);
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("token revoke requires <id>");
        return new { cmd = "token-revoke", id };
    }

    private static object BuildDrain(List<string> args, string cmd)
    {
        string? serverId = args.Count >= 2 && !args[1].StartsWith('-') ? args[1] : ServerIdOpt(args);
        if (string.IsNullOrEmpty(serverId)) throw new ArgumentException($"{cmd} requires <serverId> or --server <id>");
        return new { cmd, serverId };
    }

    // `evacuate` is the eviction half of what kubernetes calls a drain, and reads the same way as
    // `drain`: the backend positionally or under --server. Whether --to names the source, and
    // whether the pace is one the proxy will accept, are the proxy's calls rather than nimctl's,
    // so both go on the wire as typed and come back refused in the proxy's own words.
    private static Dictionary<string, object?> BuildEvacuate(List<string> args)
    {
        string? serverId = args.Count >= 2 && !args[1].StartsWith('-') ? args[1] : ServerIdOpt(args);
        if (string.IsNullOrEmpty(serverId)) throw new ArgumentException("evacuate requires <serverId> or --server <id>");

        var d = new Dictionary<string, object?> { ["cmd"] = "evacuate", ["serverId"] = serverId };

        string? to = GetOpt(args, "--to") ?? GetOpt(args, "--target");
        if (!string.IsNullOrEmpty(to)) d["to"] = to;

        string? paceStr = GetOpt(args, "--pace-ms") ?? GetOpt(args, "--paceMs");
        if (!string.IsNullOrEmpty(paceStr))
        {
            // A pace sent as a string is a field the proxy reads as absent, so it would silently
            // fall back to the default instead of running at the pace that was asked for.
            if (!int.TryParse(paceStr, out int paceMs))
                throw new ArgumentException("--pace-ms takes a number of milliseconds");
            d["paceMs"] = paceMs;
        }

        string? reason = GetOpt(args, "--reason");
        if (!string.IsNullOrEmpty(reason)) d["reason"] = reason;
        return d;
    }

    // Send arbitrary JSON straight to the admin endpoint.
    private static JsonElement BuildRaw(List<string> args)
    {
        if (args.Count < 2) throw new ArgumentException("raw requires a JSON argument");
        // Validate by re-parsing.
        using var doc = JsonDocument.Parse(args[1]);
        return JsonSerializer.Deserialize<JsonElement>(args[1]);
    }

    // How long to wait for the proxy's answer. Every command answers straight away except
    // `evacuate`, which walks a backend's sessions at a pace the operator sets and replies with
    // the summary once the sweep is done, so it gets a budget the proxy's own sweep fits inside.
    internal static TimeSpan ReadTimeout(List<string> args)
        => NormalizeCommand(args[0]) == EvacuateCommandName ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(15);

    private static async Task<string> SendAsync(string host, int port, string? secret, object payload, TimeSpan readTimeout)
    {
        using var tcp = new TcpClient { NoDelay = true };
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await tcp.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);
        var stream = tcp.GetStream();

        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var readCts = new CancellationTokenSource(readTimeout);

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

    // Where nimctl itself connects, and what is left over for the verb. Parsing stops at the first
    // token that is not one of these three name-value pairs, which is the verb: connection settings
    // before the command, verb arguments after it, exactly as the help reads. Reading them wherever
    // they appeared is what made the documented `swap <id> --host <h> --port <p>` repoint nimctl at
    // the transfer target instead of sending the player there (issue #81).
    internal static (string host, int port, string? secret, List<string> rest) ParseGlobalOptions(string[] args)
    {
        string host = Environment.GetEnvironmentVariable("NIMCTL_HOST") ?? DefaultHost;
        int port = int.TryParse(Environment.GetEnvironmentVariable("NIMCTL_PORT"), out var ep) ? ep : DefaultPort;
        string? secret = Environment.GetEnvironmentVariable("NIMCTL_SECRET");

        (host, port, secret) = ApplyConfigFile(
            new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }, host, port, secret);

        int i = 0;
        // The loop ends on the last flag with nothing after it as well as on the verb: a flag with
        // no value has none to take, so it stays in the command and comes back as an unknown
        // command rather than moving where nimctl connects.
        while (i + 1 < args.Length)
        {
            if (args[i] == "--host") host = args[i + 1];
            else if (args[i] == "--port") port = ParsePort(args[i + 1]);
            else if (args[i] == "--secret") secret = args[i + 1];
            else break;
            i += 2;
        }
        return (host, port, secret, new List<string>(args[i..]));
    }

    // --port is read on both sides of the verb and has to refuse a typo the same way in both,
    // in the shape --duration already uses rather than as a raw FormatException.
    private static int ParsePort(string value)
    {
        if (!int.TryParse(value, out int port)) throw new ArgumentException("--port takes a port number");
        return port;
    }

    // Connection flags are read before the verb only, so one typed after it would now be ignored.
    // `swap` is the only verb with a --host and --port of its own, and no verb has a --secret, so
    // anything else found here is an operator using the spelling that used to work by accident.
    // Saying so beats talking to the default proxy without a word about it.
    internal static void RejectMisplacedConnectionFlags(List<string> args)
    {
        bool verbTakesHostAndPort = NormalizeCommand(args[0]) == "swap";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var corrected = new List<string>();

        // From 1: args[0] is the verb, and a verb is never one of these.
        for (int i = 1; i < args.Count; i++)
        {
            string flag = args[i];
            bool misplaced = flag == "--secret"
                || ((flag == "--host" || flag == "--port") && !verbTakesHostAndPort);
            if (!misplaced || !seen.Add(flag)) continue;

            // The operator's own values go in the correction so it can be retyped as it stands,
            // except the secret: this message reaches stderr, and from there scrollback and logs.
            string value = flag != "--secret" && i + 1 < args.Count ? args[i + 1] : $"<{flag[2..]}>";
            corrected.Add($"{flag} {value}");
        }

        if (corrected.Count == 0) return;
        throw new ArgumentException(
            "connection flags go before the command, not after it: "
            + $"nimctl {string.Join(' ', corrected)} {args[0]} ...");
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
        "evac" => EvacuateCommandName,
        "wl" => "whitelist",
        "tokens" => "token",
        _ => cmd,
    };

    private static long RequiredLong(List<string> args, int idx, string label)
    {
        if (args.Count <= idx) throw new ArgumentException($"missing {label}");
        if (!long.TryParse(args[idx], out var v)) throw new ArgumentException($"invalid {label}: {args[idx]}");
        return v;
    }

    // Both spellings have been accepted since these verbs existed, and every verb that names a
    // backend accepts both, so the pair is one option rather than two.
    private static string? ServerIdOpt(List<string> args)
        => GetOpt(args, "--server") ?? GetOpt(args, "--serverId");

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
            return JsonSerializer.Serialize(doc.RootElement, IndentedJson);
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
        Console.WriteLine("  nimctl [--host H] [--port P] [--secret S] <command> [args]");
        Console.WriteLine();
        Console.WriteLine("  --host, --port and --secret say where nimctl connects and go before the command.");
        Console.WriteLine("  Everything after the command belongs to it, which is how the --host and --port of");
        Console.WriteLine("  `swap` name the backend a player is sent to rather than the proxy being asked.");
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
        Console.WriteLine("  evacuate <serverId> [--to <id>] [--pace-ms <n>] [--reason \"...\"]");
        Console.WriteLine("      move every player already on <serverId> somewhere else. drain stops new arrivals");
        Console.WriteLine("      and evacuate moves the ones already there, so `drain hub` then `evacuate hub`:");
        Console.WriteLine("      on its own, evacuate leaves hub open and new joins can land on it mid-sweep.");
        Console.WriteLine("      --to omitted lets the router pick per player; --pace-ms is the gap between");
        Console.WriteLine("      transfers (250 default, 0 for none). Refused players stay put and are named");
        Console.WriteLine("      in the answer, so evacuate is safe to run again.");
        Console.WriteLine("  ban (--uid <uid> | --player <name>) [--server <id>] [--duration <s>] [--reason \"...\"]");
        Console.WriteLine("      no --server bans across the whole network; --duration 0 or omitted is permanent.");
        Console.WriteLine("  unban <uid> [--server <id>]             lift a ban");
        Console.WriteLine("  bans                                    list active bans");
        Console.WriteLine("  whitelist add (--uid <uid> | --player <name>) [--server <id>] [--duration <s>] [--note \"...\"]");
        Console.WriteLine("      no --server covers the whole network; --duration 0 or omitted is permanent.");
        Console.WriteLine("  whitelist remove <uid> [--server <id>]  drop an entry, disconnecting whoever loses access");
        Console.WriteLine("  whitelist list                          list entries and where they are enforced");
        Console.WriteLine("      enforcement is [whitelist] in nimbus.proxy.toml, never the list being non-empty.");
        Console.WriteLine("  token create --name <n> --scopes <a,b> [--duration <s> | --permanent]");
        Console.WriteLine("      scopes: bans:read, bans:write, whitelist:read, whitelist:write, servers:read.");
        Console.WriteLine("      default expiry is 90 days; --permanent is the explicit opt-out.");
        Console.WriteLine("      the secret is printed once and cannot be recovered afterwards.");
        Console.WriteLine("  token revoke <id>                       revoke a token by the id `token list` shows");
        Console.WriteLine("  token list                              list issued tokens, scopes, expiry and last use");
        Console.WriteLine("      tokens authenticate over loopback or TLS only, and only when the registry has");
        Console.WriteLine("      api_tokens.enabled = true.");
        Console.WriteLine("  reload                                  reload nimbus.proxy.toml and all plugins");
        Console.WriteLine("  raw '<json>'                            send a raw JSON line (for new commands)");
        Console.WriteLine();
        Console.WriteLine("Defaults: host=127.0.0.1 port=42499.");
        Console.WriteLine();
        Console.WriteLine("Auth: pass --secret <s> before the command, set NIMCTL_SECRET, or add \"Secret\" to");
        Console.WriteLine("      nimctl.json when the proxy is configured with admin.secret.");
        Console.WriteLine("Overrides (highest wins): CLI flags > nimctl.json > NIMCTL_HOST/PORT env > built-in.");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0=ok, 1=error, 2=usage, 3=server replied ok:false.");
    }
}
