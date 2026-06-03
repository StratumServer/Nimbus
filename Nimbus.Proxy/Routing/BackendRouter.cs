using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

// Picks a backend for a new session out of the configured pool, skipping entries the registry
// reports as stale/maintenance, entries the operator has drained, and entries at capacity.
internal sealed class BackendRouter
{
    private readonly ProxyConfig cfg;
    private readonly IRegistryClient? registry;
    private readonly PersistentDrainStore? drainStore;
    private readonly HashSet<string> drained = new(StringComparer.OrdinalIgnoreCase);
    private readonly object drainLock = new();

    public BackendRouter(ProxyConfig cfg, IRegistryClient? registry, PersistentDrainStore? drainStore = null)
    {
        this.cfg = cfg;
        this.registry = registry;
        this.drainStore = drainStore;
        if (drainStore != null)
        {
            foreach (var serverId in drainStore.Load())
                drained.Add(serverId);
            if (drained.Count > 0)
                Log.Info($"router: restored drained servers [{string.Join(",", drained)}]");
            ProxyMetrics.SetDrainedServers(drained.Count);
        }
    }

    // Returns the chosen backend or null with a short reason on no-match. Convenience over
    // SelectOrderedAsync that returns just the first viable entry.
    public async Task<(BackendEndpoint? target, string? reason)> SelectAsync(CancellationToken ct)
    {
        var (ordered, none) = await SelectOrderedAsync(ct).ConfigureAwait(false);
        return ordered.Count == 0 ? (null, none) : (ordered[0], null);
    }

    // Returns the full ordered list of viable backends to attempt, plus a no-match reason
    // when the list is empty. Ordering rules:
    //   1. If top-level `try` is non-empty: walk it in order, look up each name in `Backends`.
    //      Unknown names are skipped with a warn log.
    //   2. Else if `Backends` is non-empty: use its declared order.
    //   3. Else fall back to `[DefaultBackend]`.
    // Health filtering (drain / registry stale / maintenance / capacity) is then applied to
    // the resulting list, preserving order. The DefaultBackend single-candidate path is never
    // health-gated (single-backend deployments work without the registry).
    public async Task<(IReadOnlyList<BackendEndpoint> ordered, string? noneReason)> SelectOrderedAsync(CancellationToken ct)
    {
        IReadOnlyList<BackendEndpoint> source = BuildOrderedSource();
        if (source.Count == 0) return (Array.Empty<BackendEndpoint>(), "no candidates configured");

        // No registry: accept the source as-is, just filtered by drain.
        NetworkSnapshot? snap = null;
        if (registry != null)
        {
            try { snap = await registry.GetServersAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"router: snapshot fetch failed: {ex.Message}"); }
        }
        // Treat an empty snapshot as "registry has nothing to say yet" so newly-started
        // embedded registries (no backend has heartbeat-ed yet) still route to configured
        // servers instead of returning "no viable candidates".
        if (snap != null && snap.Backends.Count == 0) snap = null;

        var viable = new List<BackendEndpoint>(source.Count);
        string? lastSkipReason = null;
        foreach (var c in source)
        {
            if (!string.IsNullOrEmpty(c.ServerId) && IsDrained(c.ServerId))
            {
                lastSkipReason = $"{c.ServerId} drained";
                continue;
            }
            if (snap == null || string.IsNullOrEmpty(c.ServerId))
            {
                // No health data for this entry. Pass it through.
                viable.Add(c);
                continue;
            }

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
            viable.Add(c);
        }

        return viable.Count == 0
            ? (Array.Empty<BackendEndpoint>(), lastSkipReason ?? "no viable candidates")
            : (viable, null);
    }

    private IReadOnlyList<BackendEndpoint> BuildOrderedSource()
    {
        var backends = cfg.Backends();
        if (cfg.Try.Count > 0)
        {
            var ordered = new List<BackendEndpoint>(cfg.Try.Count);
            foreach (var name in cfg.Try)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                BackendEndpoint? hit = null;
                foreach (var b in backends)
                {
                    if (string.Equals(b.ServerId, name, StringComparison.OrdinalIgnoreCase)) { hit = b; break; }
                }
                if (hit == null)
                {
                    Log.Warn($"router: try references unknown server '{name}', skipping");
                    continue;
                }
                ordered.Add(hit);
            }
            if (ordered.Count > 0) return ordered;
        }
        return backends;
    }

    public bool Drain(string serverId)
    {
        if (string.IsNullOrEmpty(serverId)) return false;
        lock (drainLock)
        {
            bool added = drained.Add(serverId);
            if (added) SaveDrainsLocked();
            ProxyMetrics.SetDrainedServers(drained.Count);
            return added;
        }
    }

    public bool Undrain(string serverId)
    {
        if (string.IsNullOrEmpty(serverId)) return false;
        lock (drainLock)
        {
            bool removed = drained.Remove(serverId);
            if (removed) SaveDrainsLocked();
            ProxyMetrics.SetDrainedServers(drained.Count);
            return removed;
        }
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

    private void SaveDrainsLocked()
        => drainStore?.Save(drained);

    // Read-only view of the configured candidate list, in router order.
    public IReadOnlyList<BackendEndpoint> Candidates => cfg.Backends();
}
