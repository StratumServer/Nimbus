using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, NetworkBan> _bans = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly RegistryStateFile<NetworkBan>? _state;

    public BanStore(TimeProvider? clock = null, RegistryStateFile<NetworkBan>? state = null)
    {
        _clock = clock ?? TimeProvider.System;
        _state = state;
        if (_state is null) return;

        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        bool dropped = false;
        foreach (var ban in _state.Load())
        {
            // A ban that ran out while the registry was down is over. Keeping it until the first
            // sweep would put the player back under a ban they had already served.
            if (string.IsNullOrEmpty(ban.PlayerUid) || !ban.IsActiveAt(now)) { dropped = true; continue; }
            _bans[Key(ban.PlayerUid, ban.ServerId)] = ban;
        }
        // Written back only when the file and the list disagree, so an untouched registry does
        // not rewrite its state on every boot.
        if (dropped) Persist(now);
    }

    private static string Key(string playerUid, string? serverId)
        => (playerUid ?? "").ToLowerInvariant() + "|" + (serverId ?? "").ToLowerInvariant();

    // Adds or replaces the ban for this (uid, scope). Re-banning an already-banned player
    // updates the reason and duration rather than stacking entries.
    public NetworkBan Add(NetworkBan ban)
    {
        _bans[Key(ban.PlayerUid, ban.ServerId)] = ban;
        Persist();
        return ban;
    }

    public bool Lift(string playerUid, string? serverId)
    {
        if (!_bans.TryRemove(Key(playerUid, serverId), out _)) return false;
        // Write-through on the removal too. A lift that only lived in memory would come back
        // from the file on the next restart, which is the failure nobody checks for.
        Persist();
        return true;
    }

    // Every ban that currently blocks this player from `serverId`. Pass an empty serverId to
    // ask only about network-wide bans (the connection gate). Expired entries are skipped.
    public NetworkBan? FindBlocking(string playerUid, string? serverId = null)
    {
        if (string.IsNullOrEmpty(playerUid)) return null;
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        // Kept as a walk rather than the Where/FirstOrDefault the analyser asks for: the connection
        // gate calls this on every join, and the LINQ form either copies the whole table (.Values
        // snapshots a ConcurrentDictionary) or allocates a closure per call, where this exits on
        // the first match and allocates nothing. Active() below is the operator-driven listing and
        // does get the LINQ form.
        foreach (var kv in _bans) // NOSONAR
        {
            var ban = kv.Value;
            if (!string.Equals(ban.PlayerUid, playerUid, StringComparison.OrdinalIgnoreCase)) continue;
            if (!ban.IsActiveAt(now)) continue;
            if (ban.Blocks(serverId)) return ban;
        }
        return null;
    }

    public List<NetworkBan> Active()
    {
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        return _bans.Values.Where(ban => ban.IsActiveAt(now)).ToList();
    }

    public int Prune()
    {
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        int dropped = 0;
        foreach (var kv in _bans)
        {
            if (!kv.Value.IsActiveAt(now) && _bans.TryRemove(kv.Key, out _))
                dropped++;
        }
        if (dropped > 0) Persist(now);
        return dropped;
    }

    private void Persist() => Persist(_clock.GetUtcNow().ToUnixTimeSeconds());

    private void Persist(long nowUnix) => _state?.Save(_bans.Values, nowUnix);
}
