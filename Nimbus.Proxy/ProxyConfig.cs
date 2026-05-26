using System.Text.Json.Serialization;

namespace Nimbus.Proxy;

internal sealed class ProxyConfig
{
    public string ListenHost { get; set; } = "0.0.0.0";
    public int ListenPort { get; set; } = 42420;

    public BackendEndpoint DefaultBackend { get; set; } = new();

    // Optional pool of backends. When non-empty, the router prefers the first entry whose
    // ServerId is registered and not stale/maintenance/drained, falling back through the list.
    // When empty, behaves as before: every session goes to DefaultBackend.
    public List<BackendEndpoint> Backends { get; set; } = new();

    /// <summary>If true, logs a hexdump of the first ~64 bytes of every burst to/from each side.</summary>
    public bool LogTrafficBytes { get; set; }

    /// <summary>If true, reassembles VS TCP frames and logs one line per frame (len, compressed, firstByte).</summary>
    public bool SniffFrames { get; set; }

    public int ConnectTimeoutMs { get; set; } = 5000;
    public int BufferSize { get; set; } = 16 * 1024;

    /// <summary>Bind host for the admin control endpoint. Localhost-only by default.</summary>
    public string AdminHost { get; set; } = "127.0.0.1";

    /// <summary>TCP port for line-based JSON admin commands. 0 disables.</summary>
    public int AdminPort { get; set; } = 42499;

    /// <summary>
    /// Optional shared secret required as the first admin command (<c>{"cmd":"auth","secret":"..."}</c>).
    /// When non-empty, any other first command is rejected and the connection is closed.
    /// Recommended whenever <see cref="AdminHost"/> is not localhost-only.
    /// </summary>
    public string AdminSecret { get; set; } = "";

    /// <summary>If true, TRACE-level log lines are printed. Off by default.</summary>
    public bool VerboseLogging { get; set; } = false;

    /// <summary>Optional Nimbus.Registry integration. Disabled by default.</summary>
    public NimbusConfig Nimbus { get; set; } = new();
}

internal sealed class BackendEndpoint
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 42421;

    /// <summary>
    /// Nimbus ServerId of this backend (matches the value the backend reports in heartbeats).
    /// Required for the proxy to mint a pre-swap reservation against this backend.
    /// </summary>
    public string ServerId { get; set; } = "";

    public override string ToString()
        => string.IsNullOrEmpty(ServerId) ? $"{Host}:{Port}" : $"{ServerId}@{Host}:{Port}";
}

internal sealed class NimbusConfig
{
    /// <summary>Master switch. When false, the proxy never contacts Nimbus.Registry.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Registry base URL, e.g. http://192.168.1.148:8088.</summary>
    public string RegistryUrl { get; set; } = "";

    /// <summary>HMAC shared secret. Must be present in the registry's accepted-secrets list.</summary>
    public string SharedSecret { get; set; } = "";

    /// <summary>Logical id this proxy uses as the SourceServerId on minted reservations.</summary>
    public string ProxyServerId { get; set; } = "nimbus-proxy";

    /// <summary>Per-request HTTP timeout to the registry, in seconds.</summary>
    public int RegistryHttpTimeoutSeconds { get; set; } = 5;

    /// <summary>Requested reservation lifetime, in seconds. Registry may cap.</summary>
    public int ReservationTtlSeconds { get; set; } = 60;

    /// <summary>If true, swap fails when reservation minting fails. If false, swap proceeds anyway (auth may still reject).</summary>
    public bool FailOnRegistryError { get; set; } = true;
}

[JsonSerializable(typeof(ProxyConfig))]
internal partial class ProxyConfigContext : JsonSerializerContext { }
