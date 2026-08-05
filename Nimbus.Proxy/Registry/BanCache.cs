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
    private readonly IRegistryClient? registry;
    private readonly CancellationToken stopToken;
    private readonly TimeProvider clock;
    private readonly TimeSpan refreshPeriod;

    private volatile NetworkBan[] bans = Array.Empty<NetworkBan>();

    public BanCache(IRegistryClient? registry, CancellationToken stopToken,
        TimeSpan? refreshPeriod = null, TimeProvider? clock = null)
    {
        this.registry = registry;
        this.stopToken = stopToken;
        this.clock = clock ?? TimeProvider.System;
        this.refreshPeriod = refreshPeriod ?? TimeSpan.FromSeconds(15);
    }

    public int Count => bans.Length;

    // The ban blocking this player from `serverId`, or null. A network-wide ban matches whatever
    // is asked; a scoped one only its own backend. Pass no serverId to ask about the network
    // alone, which is all a backend configured as host:port can be asked about.
    public NetworkBan? FindBlocking(string? playerUid, string? serverId = null)
    {
        if (string.IsNullOrEmpty(playerUid)) return null;
        var snapshot = bans;
        if (snapshot.Length == 0) return null;

        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        foreach (var ban in snapshot)
        {
            if (!string.Equals(ban.PlayerUid, playerUid, StringComparison.OrdinalIgnoreCase)) continue;
            // Expiry is checked here too: a timed ban must stop applying even if the next
            // refresh has not landed yet.
            if (!ban.IsActiveAt(now)) continue;
            if (ban.Blocks(serverId)) return ban;
        }
        return null;
    }

    public async Task RefreshAsync()
    {
        if (registry == null) return;
        var fetched = await registry.GetBansAsync(stopToken).ConfigureAwait(false);
        if (fetched == null) return;  // registry error: keep the previous list
        bans = fetched.ToArray();
    }

    public async Task RunAsync()
    {
        if (registry == null) return;

        while (!stopToken.IsCancellationRequested)
        {
            try { await RefreshAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Warn($"ban refresh failed: {ex.GetType().Name}: {ex.Message}"); }

            try { await Task.Delay(refreshPeriod, stopToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
