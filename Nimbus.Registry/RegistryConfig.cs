namespace Nimbus.Registry;

// Top-level registry configuration loaded from nimbus.registry.json.
public sealed class RegistryConfig
{
    // Bind address. Default binds all interfaces on the dev port.
    public string BindUrl { get; set; } = "http://0.0.0.0:8765";

    // HMAC shared secret used by every backend. To rotate, put the new secret in
    // AcceptedSecrets first, redeploy backends with the new value, then promote it here.
    public string SharedSecret { get; set; } = "change-me-and-keep-secret";

    // Additional accepted secrets during rotation. May be empty.
    public string[] AcceptedSecrets { get; set; } = Array.Empty<string>();

    // Seconds without a heartbeat before a backend is marked Stale in the snapshot.
    public int BackendStaleSeconds { get; set; } = 20;

    // Seconds without a heartbeat before a backend is dropped from the registry.
    public int BackendDropSeconds { get; set; } = 120;

    // Max age of a single nonce kept for replay protection.
    public int NonceWindowSeconds { get; set; } = 90;

    // If non-zero, refuse reservations whose TTL exceeds this many seconds.
    public int MaxReservationTtlSeconds { get; set; } = 300;

    // If true, log every successful heartbeat at Information level.
    public bool LogHeartbeats { get; set; } = false;

    // Identity advertised to the VS master server. The registry registers the whole
    // network as a single entry. Off by default.
    public ServerIdentityConfig Identity { get; set; } = new();

    public IEnumerable<string> AllSecrets()
    {
        if (!string.IsNullOrEmpty(SharedSecret)) yield return SharedSecret;
        foreach (var s in AcceptedSecrets)
            if (!string.IsNullOrEmpty(s)) yield return s;
    }
}

// Network identity published to the VS master server.
public sealed class ServerIdentityConfig
{
    // Master server advertising kill switch. Off by default.
    public bool AdvertiseOnMasterServer { get; set; } = false;

    // Vanilla master server endpoint. Override only when self-hosting.
    public string MasterServerUrl { get; set; } = "http://masterserver.vintagestory.at/api/v1/servers/";

    // Public host/port clients connect to. Must be the proxy's reachable address.
    public string PublicHost { get; set; } = "";
    public ushort PublicPort { get; set; } = 42420;

    public string ServerName { get; set; } = "Nimbus Network";
    public string ServerDescription { get; set; } = "";
    public string ServerUrl { get; set; } = "";
    public string ServerIcon { get; set; } = "";
    public string VhIdentifier { get; set; } = "";
    public string GameVersion { get; set; } = "1.22.2";

    // 0 = sum of live backend MaxPlayers.
    public int MaxPlayersOverride { get; set; } = 0;

    public PlaystyleConfig Playstyle { get; set; } = new();

    // Mod list source for the registration packet:
    //   "aggregate"          - union of every live backend's RequiredClientMods
    //   "explicit"           - use ExplicitMods
    //   "backend:<serverId>" - mirror one backend's mod list
    public string ModSource { get; set; } = "aggregate";
    public ExplicitMod[] ExplicitMods { get; set; } = Array.Empty<ExplicitMod>();

    public bool Whitelisted { get; set; } = false;
    public bool HasPassword { get; set; } = false;

    // Master server heartbeat cadence. Vanilla uses 120s; do not go lower.
    public int HeartbeatIntervalSeconds { get; set; } = 120;
}

public sealed class PlaystyleConfig
{
    public string Id { get; set; } = "surviveandbuild";
    public string LangCode { get; set; } = "preset-surviveandbuild";
}

public sealed class ExplicitMod
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
}
