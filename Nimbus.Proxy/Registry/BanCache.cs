using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

// Proxy-side snapshot of the registry's ban list.
//
// The connection gate runs while parsing Identification, on the byte pump, so it has to be a
// synchronous lookup: a registry round-trip per join would put the control plane on the hot
// path of every login. A background refresh keeps this list warm instead, and locally issued
// bans are applied immediately so an operator's ban takes effect on the next join rather than
// after the next poll.
//
// A registry outage leaves the last known list in place, so bans keep applying while it is
// down. The list is only cleared when the registry answers with an empty one.
internal sealed class BanCache
{
    private readonly RegistryEntryCache<NetworkBan> cache;

    public BanCache(IRegistryClient? registry, CancellationToken stopToken,
        TimeSpan? refreshPeriod = null, TimeProvider? clock = null)
        => cache = new RegistryEntryCache<NetworkBan>(registry,
            static (r, ct) => r.GetBansAsync(ct), "ban", stopToken, refreshPeriod, clock);

    public int Count => cache.Count;

    // The ban blocking this player from `serverId`, or null. A network-wide ban matches whatever
    // is asked; a scoped one only its own backend. Pass no serverId to ask about the network
    // alone, which is all a backend configured as host:port can be asked about.
    public NetworkBan? FindBlocking(string? playerUid, string? serverId = null)
        => cache.Find(playerUid, serverId);

    public Task RefreshAsync() => cache.RefreshAsync();

    public Task RunAsync() => cache.RunAsync();
}
