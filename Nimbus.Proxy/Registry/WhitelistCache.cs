using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

// Proxy-side snapshot of the registry's whitelist, the twin of BanCache.
//
// Same reason for existing: the connection gate runs while parsing Identification, on the byte
// pump, so the lookup has to be synchronous. A background refresh keeps this list warm, and an
// entry added through the admin socket is applied immediately so it takes effect on the next
// join rather than after the next poll.
//
// A registry outage leaves the last known list in place. That is safe for bans and dangerous
// here: with enforcement on and nothing ever fetched, an empty list means nobody gets in at
// all. HasSynced exists for exactly that case, so the gate can tell "the list really is empty"
// apart from "we have never managed to read it".
internal sealed class WhitelistCache
{
    private readonly IRegistryClient? registry;
    private readonly CancellationToken stopToken;
    private readonly TimeProvider clock;
    private readonly TimeSpan refreshPeriod;

    private volatile WhitelistEntry[] entries = Array.Empty<WhitelistEntry>();
    private volatile bool synced;

    public WhitelistCache(IRegistryClient? registry, CancellationToken stopToken,
        TimeSpan? refreshPeriod = null, TimeProvider? clock = null)
    {
        this.registry = registry;
        this.stopToken = stopToken;
        this.clock = clock ?? TimeProvider.System;
        this.refreshPeriod = refreshPeriod ?? TimeSpan.FromSeconds(15);
    }

    public int Count => entries.Length;

    // True once the registry has answered at least once since boot. False means the list below
    // is a guess, not an answer: nobody has ever been listed as far as this proxy knows.
    public bool HasSynced => synced;

    // The entry covering this player on `serverId`, or null. A network-wide entry matches
    // whatever is asked; a scoped one only its own backend. Pass no serverId to ask about the
    // network alone, which is all a backend configured as host:port can be asked about.
    public WhitelistEntry? FindCovering(string? playerUid, string? serverId = null)
    {
        if (string.IsNullOrEmpty(playerUid)) return null;
        var snapshot = entries;
        if (snapshot.Length == 0) return null;

        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        foreach (var entry in snapshot)
        {
            if (!string.Equals(entry.PlayerUid, playerUid, StringComparison.OrdinalIgnoreCase)) continue;
            // Expiry is checked here too: a timed entry must stop covering even if the next
            // refresh has not landed yet.
            if (!entry.IsActiveAt(now)) continue;
            if (entry.Covers(serverId)) return entry;
        }
        return null;
    }

    public async Task RefreshAsync()
    {
        if (registry == null) return;
        var fetched = await registry.GetWhitelistAsync(stopToken).ConfigureAwait(false);
        if (fetched == null) return;  // registry error: keep the previous list
        entries = fetched.ToArray();
        synced = true;
    }

    public async Task RunAsync()
    {
        if (registry == null) return;

        while (!stopToken.IsCancellationRequested)
        {
            try { await RefreshAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Warn($"whitelist refresh failed: {ex.GetType().Name}: {ex.Message}"); }

            try { await Task.Delay(refreshPeriod, stopToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
