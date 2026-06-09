using System.Collections.Concurrent;

namespace Nimbus.Proxy;

// Map of "this player's next reconnect should go to this backend".
//
// Redirect-style transfers stage an entry here after minting a target reservation. When the
// player reconnects, ClientSessionRunner parses the first Identification frame and routes the
// session to the staged target before any upstream is opened.
//
// Entries are single-use. TryConsume removes the entry on a match. Expired entries are dropped
// by TryConsume on access and proactively by SweepExpired.
internal sealed class StickyRouteTable
{
    private readonly record struct Entry(BackendEndpoint Target, DateTime ExpiresAtUtc, string Reason);

    private readonly ConcurrentDictionary<string, Entry> _byUid =
        new(StringComparer.OrdinalIgnoreCase);

    // Stage a sticky route. Overwrites any prior entry for the same uid.
    public void Stage(string playerUid, BackendEndpoint target, TimeSpan ttl, string reason)
    {
        if (string.IsNullOrEmpty(playerUid)) return;
        _byUid[playerUid] = new Entry(target, DateTime.UtcNow + ttl, reason ?? "");
    }

    // Returns true and removes the entry if a non-expired sticky route exists. Expired entries
    // are dropped and treated as no match.
    public bool TryConsume(string playerUid, out BackendEndpoint target, out string reason)
    {
        target = default!;
        reason = "";
        if (string.IsNullOrEmpty(playerUid)) return false;
        if (!_byUid.TryRemove(playerUid, out var e)) return false;
        if (e.ExpiresAtUtc < DateTime.UtcNow) return false;
        target = e.Target;
        reason = e.Reason;
        return true;
    }

    // Read-only peek for diagnostics. Does not remove the entry.
    public bool Peek(string playerUid, out BackendEndpoint target, out DateTime expiresAtUtc, out string reason)
    {
        target = default!;
        expiresAtUtc = default;
        reason = "";
        if (string.IsNullOrEmpty(playerUid)) return false;
        if (!_byUid.TryGetValue(playerUid, out var e)) return false;
        target = e.Target;
        expiresAtUtc = e.ExpiresAtUtc;
        reason = e.Reason;
        return true;
    }

    // Snapshot of currently-staged routes for the `sticky` admin command.
    public IReadOnlyList<(string Uid, BackendEndpoint Target, DateTime ExpiresAtUtc, string Reason)> Snapshot()
    {
        var now = DateTime.UtcNow;
        var list = new List<(string, BackendEndpoint, DateTime, string)>(_byUid.Count);
        foreach (var kv in _byUid)
        {
            if (kv.Value.ExpiresAtUtc < now) continue;
            list.Add((kv.Key, kv.Value.Target, kv.Value.ExpiresAtUtc, kv.Value.Reason));
        }
        return list;
    }

    // Drop expired entries. Safe to call on a timer.
    public int SweepExpired()
    {
        var now = DateTime.UtcNow;
        int removed = 0;
        foreach (var kv in _byUid)
        {
            if (kv.Value.ExpiresAtUtc < now && _byUid.TryRemove(kv.Key, out _))
                removed++;
        }
        return removed;
    }
}
