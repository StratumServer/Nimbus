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
            string cmd = rest[0];
            object payload = cmd switch
            {
                "ping"    => new { cmd = "ping" },
                "list"    => new { cmd = "list" },
                "status"  => BuildStatus(rest),
                "kick"    => BuildKick(rest),
                "servers" => BuildServers(rest),
                "swap"    => BuildSwap(rest),
                "sticky"  => new { cmd = "sticky" },
                "raw"     => BuildRaw(rest),
                _ => throw new ArgumentException($"unknown command: {cmd}"),
            };

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
        string? ttlStr = GetOpt(args, "--ttl");
        bool splice = args.Contains("--splice");
        bool redirect = args.Contains("--redirect");
        bool disconnect = args.Contains("--disconnect");
        int modeCount = (splice ? 1 : 0) + (redirect ? 1 : 0) + (disconnect ? 1 : 0);
        if (modeCount > 1) throw new ArgumentException("--splice, --redirect, and --disconnect are mutually exclusive");

        if (string.IsNullOrEmpty(serverId) && (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(portStr)))
            throw new ArgumentException("swap requires either --server <id> or both --host and --port");

        var d = new Dictionary<string, object?> { ["cmd"] = "swap", ["id"] = id };
        if (!string.IsNullOrEmpty(serverId)) d["serverId"] = serverId;
        if (!string.IsNullOrEmpty(host))     d["host"] = host;
        if (!string.IsNullOrEmpty(portStr))  d["port"] = int.Parse(portStr);
        if (!string.IsNullOrEmpty(reason))   d["reason"] = reason;
        if (splice)     d["mode"] = "splice";
        if (redirect)   d["mode"] = "redirect";
        if (disconnect) d["mode"] = "disconnect";
        if (!string.IsNullOrEmpty(ttlStr))
        {
            if (!int.TryParse(ttlStr, out var ttl)) throw new ArgumentException($"invalid --ttl: {ttlStr}");
            d["stickyTtlSeconds"] = ttl;
        }
        return d;
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

        // Only send auth when explicitly configured. Proxies without AdminSecret would just
        // reply with "unknown cmd" otherwise.
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

        string json = payload is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);

        var line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
        return line ?? "";
    }

    private static (string host, int port, string? secret, List<string> rest) ParseGlobalOptions(string[] args)
    {
        string host = Environment.GetEnvironmentVariable("NIMCTL_HOST") ?? DefaultHost;
        int port = int.TryParse(Environment.GetEnvironmentVariable("NIMCTL_PORT"), out var ep) ? ep : DefaultPort;
        string? secret = Environment.GetEnvironmentVariable("NIMCTL_SECRET");

        // nimctl.json in CWD or exe dir. { "Host": "...", "Port": ..., "Secret": "..." }
        foreach (var dir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
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

    private static bool IsHelp(string s) => s is "-h" or "--help" or "help";

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

    private static string PrettyPrint(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "(no response)";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch { return raw; }
    }

    private static int ExitCodeFromResponse(string raw)
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
        Console.WriteLine("  list                                    list active sessions");
        Console.WriteLine("  status <id>                             session detail (uid, phase, captured ident)");
        Console.WriteLine("  kick <id>                               force-close a session");
        Console.WriteLine("  servers [--refresh]                     dump registry snapshot");
        Console.WriteLine("  swap <id> --server <serverId> [--reason \"...\"] [--splice|--redirect|--disconnect] [--ttl <s>]");
        Console.WriteLine("  swap <id> --host <h> --port <p> [--reason \"...\"] [--splice|--redirect|--disconnect] [--ttl <s>]");
        Console.WriteLine("      default mode is --redirect (client reconnects automatically).");
        Console.WriteLine("      --disconnect : kick + sticky route, one Reconnect click on the client.");
        Console.WriteLine("      --splice     : in-place TCP swap, pre-Ready only.");
        Console.WriteLine("      --ttl <s>    : disconnect mode only, sticky route lifetime in seconds (default 300).");
        Console.WriteLine("  sticky                                  list staged disconnect-transfer routes");
        Console.WriteLine("  raw '<json>'                            send a raw JSON line (for new commands)");
        Console.WriteLine();
        Console.WriteLine("Defaults: host=127.0.0.1 port=42499.");
        Console.WriteLine();
        Console.WriteLine("Auth: pass --secret <s>, set NIMCTL_SECRET, or add \"Secret\" to nimctl.json when the");
        Console.WriteLine("      proxy is configured with AdminSecret.");
        Console.WriteLine("Overrides (highest wins): CLI flags > nimctl.json > NIMCTL_HOST/PORT env > built-in.");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0=ok, 1=error, 2=usage, 3=server replied ok:false.");
    }
}
