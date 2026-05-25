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

    public IEnumerable<string> AllSecrets()
    {
        if (!string.IsNullOrEmpty(SharedSecret)) yield return SharedSecret;
        foreach (var s in AcceptedSecrets)
            if (!string.IsNullOrEmpty(s)) yield return s;
    }
}
