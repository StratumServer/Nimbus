using Nimbus.Shared.Models;

namespace Nimbus.Registry.Services;

// Network ban list. One entry per (PlayerUid, ServerId) pair, so a player can hold a
// network-wide ban and per-backend bans at the same time. Timed bans expire on read and are
// dropped by the background sweep.
//
// Given a state file, the list also survives a restart: it is read once here and written back
// whole on every change. Without one the store is memory-only, which is what the tests and any
// caller that does not want a file on disk get.
public sealed class BanStore
{
    private readonly RegistryEntryStore<NetworkBan> _store;

    public BanStore(TimeProvider? clock = null, RegistryStateFile<NetworkBan>? state = null)
        => _store = new RegistryEntryStore<NetworkBan>(clock, state);

    // Adds or replaces the ban for this (uid, scope). Re-banning an already-banned player
    // updates the reason and duration rather than stacking entries.
    public NetworkBan Add(NetworkBan ban) => _store.Add(ban);

    public bool Lift(string playerUid, string? serverId) => _store.Remove(playerUid, serverId);

    // Every ban that currently blocks this player from `serverId`. Pass an empty serverId to
    // ask only about network-wide bans (the connection gate). Expired entries are skipped.
    public NetworkBan? FindBlocking(string playerUid, string? serverId = null)
        => _store.Find(playerUid, serverId);

    public List<NetworkBan> Active() => _store.Active();

    public int Prune() => _store.Prune();
}
