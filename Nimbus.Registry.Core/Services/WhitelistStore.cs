using Nimbus.Shared.Models;

namespace Nimbus.Registry.Services;

// Network whitelist. One entry per (PlayerUid, ServerId) pair, so a player can hold a
// network-wide entry and per-backend entries at the same time. Timed entries expire on read
// and are dropped by the background sweep. Shaped like BanStore because the gate is the same
// lookup read the other way round.
//
// The registry does not know whether any proxy is enforcing this list: the [whitelist] switches
// live in proxy config. Storing an entry is therefore never an enforcement decision.
// Given a state file, the list survives a restart the same way the ban list does: read once
// here, written back whole on every change. A whitelist that emptied itself on restart would
// lock every player out of a closed network instead of letting a griefer back in, so the two
// stores get the same treatment for opposite reasons.
public sealed class WhitelistStore
{
    private readonly RegistryEntryStore<WhitelistEntry> _store;

    public WhitelistStore(TimeProvider? clock = null, RegistryStateFile<WhitelistEntry>? state = null)
        => _store = new RegistryEntryStore<WhitelistEntry>(clock, state);

    // Adds or replaces the entry for this (uid, scope). Re-adding an already-listed player
    // updates the note and duration rather than stacking entries.
    public WhitelistEntry Add(WhitelistEntry entry) => _store.Add(entry);

    public bool Remove(string playerUid, string? serverId) => _store.Remove(playerUid, serverId);

    // The entry covering this player on `serverId`, or null. Pass an empty serverId to ask only
    // about network-wide coverage. Expired entries are skipped.
    public WhitelistEntry? FindCovering(string playerUid, string? serverId = null)
        => _store.Find(playerUid, serverId);

    public List<WhitelistEntry> Active() => _store.Active();

    public int Prune() => _store.Prune();
}
