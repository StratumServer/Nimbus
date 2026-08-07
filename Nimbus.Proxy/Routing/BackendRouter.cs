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

        NetworkSnapshot? snap = await FetchHealthSnapshotAsync(ct).ConfigureAwait(false);

        var viable = new List<BackendEndpoint>(source.Count);
        string? lastSkipReason = null;
        foreach (var c in source)
        {
            string? skip = HealthSkipReason(c, snap);
            if (skip != null) { lastSkipReason = skip; continue; }
            viable.Add(c);
        }

        return viable.Count == 0
            ? (Array.Empty<BackendEndpoint>(), lastSkipReason ?? "no viable candidates")
            : (viable, null);
    }

    // Fetches the registry's view of backend health for this routing decision, or null when there
    // is nothing to gate on: no registry configured, the fetch failed, or the snapshot is empty.
    // An empty snapshot is treated as "the registry has nothing to say yet" so a newly-started
    // embedded registry (no backend has heartbeat-ed) still routes to configured servers instead
    // of reporting no viable candidates.
    private async Task<NetworkSnapshot?> FetchHealthSnapshotAsync(CancellationToken ct)
    {
        if (registry == null) return null;
        NetworkSnapshot? snap = null;
        try { snap = await registry.GetServersAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn($"router: snapshot fetch failed: {ex.Message}"); }
        return snap is { Backends.Count: 0 } ? null : snap;
    }

    // Why this candidate should be skipped for the current session, or null to keep it. Drain is
    // checked first and applies without any health data; the remaining gates need a snapshot and a
    // ServerId, and an entry we have no health data for is passed through rather than dropped.
    private string? HealthSkipReason(BackendEndpoint c, NetworkSnapshot? snap)
    {
        if (!string.IsNullOrEmpty(c.ServerId) && IsDrained(c.ServerId)) return $"{c.ServerId} drained";
        if (snap == null || string.IsNullOrEmpty(c.ServerId)) return null;

        var b = snap.Backends.FirstOrDefault(x => string.Equals(x.ServerId, c.ServerId, StringComparison.OrdinalIgnoreCase));
        if (b == null) return $"{c.ServerId} not in registry";
        if (b.Stale) return $"{c.ServerId} stale";
        if (b.Maintenance) return $"{c.ServerId} in maintenance";
        if (b.MaxPlayers > 0 && b.Players >= b.MaxPlayers) return $"{c.ServerId} full ({b.Players}/{b.MaxPlayers})";
        return null;
    }

    private IReadOnlyList<BackendEndpoint> BuildOrderedSource()
    {
        var backends = cfg.Backends();
        var fromTry = ResolveTryList(backends);
        return fromTry.Count > 0 ? fromTry : backends;
    }

    // Resolves the top-level `try` list into backends in the order it names them, skipping blanks
    // and names that match no configured backend (with a warn). Returns an empty list when `try`
    // is unset or resolves to nothing, which is the caller's signal to fall back to declared order.
    private IReadOnlyList<BackendEndpoint> ResolveTryList(IReadOnlyList<BackendEndpoint> backends)
    {
        if (cfg.Try.Count == 0) return Array.Empty<BackendEndpoint>();
        var ordered = new List<BackendEndpoint>(cfg.Try.Count);
        foreach (var name in cfg.Try)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var hit = backends.FirstOrDefault(b => string.Equals(b.ServerId, name, StringComparison.OrdinalIgnoreCase));
            if (hit == null)
            {
                Log.Warn($"router: try references unknown server '{name}', skipping");
                continue;
            }
            ordered.Add(hit);
        }
        return ordered;
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
