using System.Net;

namespace Nimbus.Proxy;

// Velocity-shaped proxy config. Loaded from nimbus.proxy.toml next to the binary. A single
// flat-ish layout where named servers are a dict (name -> "host:port") and the connect order
// is a top-level `try` list, mirroring how a Minecraft server.properties + velocity.toml read.
//
// Example:
//   bind = "0.0.0.0:42420"
//
//   [servers]
//   hub = "127.0.0.1:42421"
//   factions = "127.0.0.1:42422"
//
//   try = [ "hub" ]
//
//   [transfers]
//   default_mode = "redirect"
//   allow_seamless = false
//
//   [admin]
//   bind = "127.0.0.1:42499"
//   secret = ""
//
//   [registry]
//   mode = "disabled"          # "embedded" | "remote" | "disabled"
internal sealed class ProxyConfig
{
    public string Bind { get; set; } = "0.0.0.0:42420";

    // Named backend pool. Key = serverId. Value = "host:port". Case-insensitive on lookup.
    public Dictionary<string, string> Servers { get; set; } = new()
    {
        ["default"] = "127.0.0.1:42421",
    };

    // Ordered connect attempts on initial join.
    // Unknown names are skipped with a warning.
    public List<string> Try { get; set; } = new() { "default" };

    // Server names that should receive a PROXY protocol v2 header on every upstream TCP.
    // The backend must list this proxy's IP in its trusted-proxy CIDRs or it will reject the
    // connection. Opt-in so unmodded backends still accept plain TCP.
    public List<string> ProxyProtocolServers { get; set; } = new();

    // Reserved for the SNI / direct-connect hostname routing pass. Map of incoming hostname
    // -> ordered try-list of server names. Not consumed yet (no SNI source in VS handshake).
    public Dictionary<string, List<string>> ForcedHosts { get; set; } = new();

    public TransfersConfig Transfers { get; set; } = new();
    public AdminConfig Admin { get; set; } = new();
    public RegistryConfig Registry { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public MetricsConfig Metrics { get; set; } = new();
    public PersistenceConfig Persistence { get; set; } = new();
    public AdvancedConfig Advanced { get; set; } = new();

    // --- Runtime helpers (getter-only / methods, so Tomlyn ignores them on serialization). ---

    private List<BackendEndpoint>? _resolvedBackends;

    public IReadOnlyList<BackendEndpoint> Backends()
    {
        if (_resolvedBackends != null) return _resolvedBackends;
        var list = new List<BackendEndpoint>(Servers.Count);
        foreach (var kv in Servers)
        {
            var (h, p) = SplitHostPort(kv.Value, $"servers.{kv.Key}");
            bool pp = false;
            foreach (var n in ProxyProtocolServers)
                if (string.Equals(n, kv.Key, StringComparison.OrdinalIgnoreCase)) { pp = true; break; }
            list.Add(new BackendEndpoint { Host = h, Port = p, ServerId = kv.Key, ProxyProtocol = pp });
        }
        _resolvedBackends = list;
        return list;
    }

    public BackendEndpoint? FindBackend(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var b in Backends())
            if (string.Equals(b.ServerId, name, StringComparison.OrdinalIgnoreCase))
                return b;
        return null;
    }

    public BackendEndpoint DefaultBackend()
    {
        var list = Backends();
        if (list.Count == 0) throw new InvalidOperationException("no backends configured in [servers]");
        return list[0];
    }

    public IPEndPoint ListenEndPoint()
    {
        var (h, p) = SplitHostPort(Bind, "bind");
        return new IPEndPoint(IPAddress.Parse(h), p);
    }

    private static (string host, int port) SplitHostPort(string s, string label)
    {
        if (string.IsNullOrWhiteSpace(s)) throw new InvalidDataException($"{label}: empty");
        int idx = s.LastIndexOf(':');
        if (idx <= 0 || idx == s.Length - 1) throw new InvalidDataException($"{label}: must be 'host:port', got '{s}'");
        string host = s.Substring(0, idx);
        if (!int.TryParse(s.AsSpan(idx + 1), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int port))
            throw new InvalidDataException($"{label}: invalid port in '{s}'");
        if (port <= 0 || port > 65535) throw new InvalidDataException($"{label}: port out of range in '{s}'");
        return (host, port);
    }
}

internal sealed class BackendEndpoint
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 42421;

    // Logical name. Matches the key in `[servers]`. Used for sticky routes and reservations.
    public string ServerId { get; set; } = "";

    // Set from ProxyProtocolServers at resolve time.
    public bool ProxyProtocol { get; set; } = false;

    public override string ToString()
        => string.IsNullOrEmpty(ServerId) ? $"{Host}:{Port}" : $"{ServerId}@{Host}:{Port}";
}

internal sealed class TransfersConfig
{
    // "redirect" (vanilla reconnect, needs RedirectFix client mod) or "seamless" (mid-session
    // splice, needs the Nimbus client+server mod).
    public string DefaultMode { get; set; } = "redirect";

    // Master switch for seamless transfers. Off by default until the Nimbus mod exists. Even
    // when true, a seamless transfer to a backend that doesn't speak the Nimbus mod protocol
    // tends to corrupt world state on the client.
    public bool AllowSeamless { get; set; } = false;
}

internal sealed class AdminConfig
{
    // "host:port" for the line-JSON admin socket. Localhost-only by default.
    public string Bind { get; set; } = "127.0.0.1:42499";

    // When non-empty the first admin frame must be {"cmd":"auth","secret":"..."}. Required
    // whenever Bind is not loopback.
    public string Secret { get; set; } = "";

    // Permissions granted after the admin secret succeeds.
    // "*" keeps today's operator model.
    public List<string> GrantedPermissions { get; set; } = new() { "*" };

    // Set false to disable the admin socket entirely.
    public bool Enabled { get; set; } = true;

    public IPEndPoint EndPoint()
    {
        if (string.IsNullOrWhiteSpace(Bind)) throw new InvalidDataException("admin.bind: empty");
        int idx = Bind.LastIndexOf(':');
        if (idx <= 0 || idx == Bind.Length - 1) throw new InvalidDataException($"admin.bind: must be 'host:port', got '{Bind}'");
        string host = Bind.Substring(0, idx);
        if (!int.TryParse(Bind.AsSpan(idx + 1), out int port)) throw new InvalidDataException($"admin.bind: invalid port in '{Bind}'");
        return new IPEndPoint(IPAddress.Parse(host), port);
    }
}

internal sealed class RegistryConfig
{
    // "embedded" -> proxy hosts the registry in-process (and optionally serves HTTP for
    //               external backends to heartbeat against).
    // "remote"   -> proxy talks to a standalone Nimbus.Registry over HTTP.
    // "disabled" -> no registry. Single-backend deployments work via [servers].
    public string Mode { get; set; } = "disabled";

    // Common to embedded + remote. SourceServerId on minted reservations.
    public string ProxyId { get; set; } = "nimbus-proxy";
    public int ReservationTtlSeconds { get; set; } = 60;
    public bool FailOnError { get; set; } = true;
    public int TransferIntentPollMs { get; set; } = 1000;

    // Remote mode only.
    public string Url { get; set; } = "";
    public string SharedSecret { get; set; } = "";
    public int HttpTimeoutSeconds { get; set; } = 5;

    // Embedded mode only. Empty Bind disables the HTTP listener.
    // The proxy still keeps its in-process registry path.
    public string EmbeddedBind { get; set; } = "http://0.0.0.0:8765";
    public string EmbeddedSharedSecret { get; set; } = "change-me-and-keep-secret";
    public int BackendStaleSeconds { get; set; } = 20;
    public int BackendDropSeconds { get; set; } = 120;
    public int NonceWindowSeconds { get; set; } = 90;
    public int MaxReservationTtlSeconds { get; set; } = 300;
    public bool AdvertiseOnMasterServer { get; set; } = false;
}

internal sealed class LoggingConfig
{
    public bool Verbose { get; set; } = false;
    public bool SniffFrames { get; set; } = false;
    public bool LogTrafficBytes { get; set; } = false;
}

internal sealed class MetricsConfig
{
    public bool Enabled { get; set; } = true;
    public string Bind { get; set; } = "http://127.0.0.1:42500";
    public string Path { get; set; } = "/metrics";
}

internal sealed class PersistenceConfig
{
    public bool PersistDrainFlags { get; set; } = true;

    // Relative paths resolve next to the proxy executable.
    public string DrainFlagsFile { get; set; } = "nimbus.drain-state.json";
}

internal sealed class AdvancedConfig
{
    public int ConnectTimeoutMs { get; set; } = 5000;
    public int BufferSize { get; set; } = 16 * 1024;
}
