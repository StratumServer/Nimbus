namespace Nimbus.Proxy;

// A staged reconnect route, handed back whole when it is consumed so the caller can tell what
// it picked up. Attempts counts the redirects already fired to put this player on Target, which
// is what stops a route that keeps missing from bouncing the session forever.
internal sealed record StickyRoute(string Uid, string ClientIp, BackendEndpoint Target, string Reason, int Attempts);

// Map of "this player's next reconnect should go to this backend".
//
// Redirect-style transfers stage an entry here after minting a target reservation. When the
// player reconnects, ClientSessionRunner consumes the entry and routes the session to the
// staged target before any upstream is opened.
//
// Entries are indexed by player UID and by client IP. Two keys because a stock Vintage Story
// client opens its connection with LoginTokenQuery, an empty packet with no identity in it, and
// only sends Identification once the backend has answered. Matching on the UID alone therefore
// misses every real client and sends it to the default backend instead of the transfer target
// (#57). The IP key covers that case; the UID key stays authoritative when the first frame does
// carry one.
//
// Entries are single-use. Consuming one removes it from both indexes. Expired entries are
// dropped on access and proactively by SweepExpired.
internal sealed class StickyRouteTable
{
    // How long a staged route stays valid under its UID key. Long enough to cover a player who
    // takes their time getting back through the launcher after a redirect.
    public static readonly TimeSpan UidTtl = TimeSpan.FromMinutes(5);

    // The IP key is much weaker than the UID key: NAT shares one address between players and
    // DHCP recycles it between people. A redirect reconnect completes in a second or two, so an
    // IP entry still sitting here a minute later is stale by definition and must not be allowed
    // to catch a stranger who happens to dial in from the same address.
    public static readonly TimeSpan IpTtl = TimeSpan.FromSeconds(60);

    private sealed class Entry
    {
        public string Uid = "";
        public string ClientIp = "";
        public BackendEndpoint Target = default!;
        public DateTime StagedAtUtc;
        public DateTime ExpiresAtUtc;
        public DateTime IpExpiresAtUtc;
        public string Reason = "";
        public int Attempts;

        public StickyRoute ToRoute() => new(Uid, ClientIp, Target, Reason, Attempts);
    }

    // One lock over both indexes. The table holds one entry per in-flight transfer, and every
    // operation on it happens once per transfer or once per connection, so contention is not a
    // concern and keeping the two indexes consistent matters more.
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> byUid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Entry>> byIp = new(StringComparer.OrdinalIgnoreCase);

    // Stage a sticky route. Overwrites any prior entry for the same uid. `clientIp` may be null
    // or empty when the caller has no endpoint to offer, in which case only the UID index is
    // written and the route can only be matched by a client that identifies first.
    public void Stage(string playerUid, string? clientIp, BackendEndpoint target, TimeSpan ttl, string reason, int attempts = 1)
    {
        if (string.IsNullOrEmpty(playerUid)) return;
        var now = DateTime.UtcNow;
        var entry = new Entry
        {
            Uid = playerUid,
            ClientIp = clientIp ?? "",
            Target = target,
            StagedAtUtc = now,
            ExpiresAtUtc = now + ttl,
            IpExpiresAtUtc = now + (ttl < IpTtl ? ttl : IpTtl),
            Reason = reason ?? "",
            Attempts = attempts,
        };

        lock (gate)
        {
            if (byUid.TryGetValue(playerUid, out var previous))
                RemoveFromIpIndex(previous);
            byUid[playerUid] = entry;
            if (entry.ClientIp.Length > 0)
            {
                if (!byIp.TryGetValue(entry.ClientIp, out var list))
                    byIp[entry.ClientIp] = list = new List<Entry>(1);
                list.Add(entry);
            }
        }
    }

    // Returns true and removes the entry if a non-expired sticky route exists for this uid.
    // Expired entries are dropped and treated as no match.
    public bool TryConsume(string playerUid, out StickyRoute route)
    {
        route = default!;
        if (string.IsNullOrEmpty(playerUid)) return false;
        lock (gate)
        {
            if (!byUid.TryGetValue(playerUid, out var entry)) return false;
            byUid.Remove(playerUid);
            RemoveFromIpIndex(entry);
            if (entry.ExpiresAtUtc < DateTime.UtcNow) return false;
            route = entry.ToRoute();
            return true;
        }
    }

    // Returns true and removes the entry if a non-expired sticky route was staged for this
    // client address. When several routes share the address (players behind one NAT transferring
    // in the same window) the oldest one is handed out: it is the one whose reconnect is most
    // likely to be arriving now. A wrong pick is not fatal, see ProxySession for the cascade
    // that repairs it.
    public bool TryConsumeByClientIp(string clientIp, out StickyRoute route)
    {
        route = default!;
        if (string.IsNullOrEmpty(clientIp)) return false;
        var now = DateTime.UtcNow;
        lock (gate)
        {
            if (!byIp.TryGetValue(clientIp, out var list)) return false;

            Entry? oldest = null;
            foreach (var candidate in list)
            {
                if (candidate.IpExpiresAtUtc < now) continue;
                if (oldest == null || candidate.StagedAtUtc < oldest.StagedAtUtc) oldest = candidate;
            }
            if (oldest == null)
            {
                // Every entry under this address has aged out of the IP window. Drop the index
                // bucket; the UID entries live on until their own, longer, TTL.
                byIp.Remove(clientIp);
                return false;
            }

            byUid.Remove(oldest.Uid);
            RemoveFromIpIndex(oldest);
            route = oldest.ToRoute();
            return true;
        }
    }

    // Read-only peek for diagnostics and tests. Does not remove the entry.
    public bool Peek(string playerUid, out BackendEndpoint target, out DateTime expiresAtUtc, out string reason)
    {
        target = default!;
        expiresAtUtc = default;
        reason = "";
        if (string.IsNullOrEmpty(playerUid)) return false;
        lock (gate)
        {
            if (!byUid.TryGetValue(playerUid, out var e)) return false;
            target = e.Target;
            expiresAtUtc = e.ExpiresAtUtc;
            reason = e.Reason;
            return true;
        }
    }

    // Snapshot of currently-staged routes for the `sticky` admin command.
    public IReadOnlyList<(string Uid, string ClientIp, BackendEndpoint Target, DateTime ExpiresAtUtc, string Reason, int Attempts)> Snapshot()
    {
        var now = DateTime.UtcNow;
        lock (gate)
        {
            var list = new List<(string, string, BackendEndpoint, DateTime, string, int)>(byUid.Count);
            foreach (var kv in byUid)
            {
                if (kv.Value.ExpiresAtUtc < now) continue;
                list.Add((kv.Key, kv.Value.ClientIp, kv.Value.Target, kv.Value.ExpiresAtUtc, kv.Value.Reason, kv.Value.Attempts));
            }
            return list;
        }
    }

    // Drop expired entries. Safe to call on a timer. IP references die first, on the shorter
    // window, so a route can outlive its address match and still be usable by UID.
    public int SweepExpired()
    {
        var now = DateTime.UtcNow;
        int removed = 0;
        lock (gate)
        {
            foreach (var uid in byUid.Where(kv => kv.Value.ExpiresAtUtc < now).Select(kv => kv.Key).ToList())
            {
                if (byUid.Remove(uid, out var entry))
                {
                    RemoveFromIpIndex(entry);
                    removed++;
                }
            }

            foreach (var ip in byIp.Keys.ToList())
            {
                var list = byIp[ip];
                removed += list.RemoveAll(e => e.IpExpiresAtUtc < now);
                if (list.Count == 0) byIp.Remove(ip);
            }
        }
        return removed;
    }

    private void RemoveFromIpIndex(Entry entry)
    {
        if (entry.ClientIp.Length == 0) return;
        if (!byIp.TryGetValue(entry.ClientIp, out var list)) return;
        list.Remove(entry);
        if (list.Count == 0) byIp.Remove(entry.ClientIp);
    }
}
