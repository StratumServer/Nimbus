using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

// Picks a backend for a new session out of the configured pool, skipping entries the registry
// reports as stale/maintenance, entries the operator has drained, and entries at capacity.
// Drain state is in-memory only (no persistence across restarts).
internal sealed class BackendRouter
{
    private readonly ProxyConfig cfg;
    private readonly RegistryClient? registry;
    private readonly HashSet<string> drained = new(StringComparer.OrdinalIgnoreCase);
    private readonly object drainLock = new();

    public BackendRouter(ProxyConfig cfg, RegistryClient? registry)
    {
        this.cfg = cfg;
        this.registry = registry;
    }

    // Returns the chosen backend or null with a short reason on no-match. Uses Backends if
    // configured, else falls back to DefaultBackend (which is never gated by registry checks
    // when used as the only candidate, so single-backend deployments keep working without
    // registry integration).
    public async Task<(BackendEndpoint? target, string? reason)> SelectAsync(CancellationToken ct)
    {
        IReadOnlyList<BackendEndpoint> candidates = cfg.Backends.Count > 0
            ? cfg.Backends
            : new[] { cfg.DefaultBackend };

        NetworkSnapshot? snap = null;
        if (registry != null)
        {
            try { snap = await registry.GetServersAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"router: snapshot fetch failed: {ex.Message}"); }
        }

        string? lastSkipReason = null;
        foreach (var c in candidates)
        {
            if (!string.IsNullOrEmpty(c.ServerId) && IsDrained(c.ServerId))
            {
                lastSkipReason = $"{c.ServerId} drained";
                continue;
            }

            // No registry or no ServerId on the entry: we can't health-check, so accept it.
            // This is the path single-backend / legacy deployments take.
            if (snap == null || string.IsNullOrEmpty(c.ServerId))
                return (c, null);

            BackendSnapshot? b = null;
            foreach (var x in snap.Backends)
            {
                if (string.Equals(x.ServerId, c.ServerId, StringComparison.OrdinalIgnoreCase))
                { b = x; break; }
            }
            if (b == null) { lastSkipReason = $"{c.ServerId} not in registry"; continue; }
            if (b.Stale) { lastSkipReason = $"{c.ServerId} stale"; continue; }
            if (b.Maintenance) { lastSkipReason = $"{c.ServerId} in maintenance"; continue; }
            if (b.MaxPlayers > 0 && b.Players >= b.MaxPlayers) { lastSkipReason = $"{c.ServerId} full ({b.Players}/{b.MaxPlayers})"; continue; }
            return (c, null);
        }

        return (null, lastSkipReason ?? "no candidates configured");
    }

    public bool Drain(string serverId)
    {
        if (string.IsNullOrEmpty(serverId)) return false;
        lock (drainLock) return drained.Add(serverId);
    }

    public bool Undrain(string serverId)
    {
        if (string.IsNullOrEmpty(serverId)) return false;
        lock (drainLock) return drained.Remove(serverId);
    }

    public bool IsDrained(string serverId)
    {
        if (string.IsNullOrEmpty(serverId)) return false;
        lock (drainLock) return drained.Contains(serverId);
    }

    public string[] ListDrained()
    {
        lock (drainLock) return drained.ToArray();
    }

    // Read-only view of the configured candidate list, in router order.
    public IReadOnlyList<BackendEndpoint> Candidates =>
        cfg.Backends.Count > 0 ? cfg.Backends : new[] { cfg.DefaultBackend };
}
