using System.Collections.Concurrent;
using System.Net;

namespace Nimbus.Proxy;

// Per-client-IP UDP routing overrides. Shared between the TCP layer (which knows when a
// session has been swapped) and the UDP relay (which sees raw client endpoints and needs
// to know which backend their datagrams should go to).
//
// Keyed by client IPAddress, not full endpoint, because TCP and UDP source ports differ.
// The IP is the only stable correlator. Assumes one player per source IP (typical home or
// LAN). Supporting multiple players per IP would require correlating via the LoginToken in
// the first UDP packet.
internal sealed class UdpRouteOverrides
{
    private readonly ConcurrentDictionary<IPAddress, BackendEndpoint> _byIp = new();

    // Install or update the backend for a client IP. Called by TCP swap.
    public void Set(IPAddress clientIp, BackendEndpoint target) => _byIp[clientIp] = target;

    // Look up the current override for a client IP. Returns true if set.
    public bool TryGet(IPAddress clientIp, out BackendEndpoint target)
    {
        if (_byIp.TryGetValue(clientIp, out var t)) { target = t; return true; }
        target = default!;
        return false;
    }

    // Remove the override (when the TCP session closes).
    public void Clear(IPAddress clientIp) => _byIp.TryRemove(clientIp, out _);

    // Diagnostics snapshot.
    public IReadOnlyList<(IPAddress Ip, BackendEndpoint Target)> Snapshot()
    {
        var list = new List<(IPAddress, BackendEndpoint)>(_byIp.Count);
        foreach (var kv in _byIp) list.Add((kv.Key, kv.Value));
        return list;
    }
}
