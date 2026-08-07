using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, WhitelistEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly RegistryStateFile<WhitelistEntry>? _state;

    public WhitelistStore(TimeProvider? clock = null, RegistryStateFile<WhitelistEntry>? state = null)
    {
        _clock = clock ?? TimeProvider.System;
        _state = state;
        if (_state is null) return;

        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        bool dropped = false;
        foreach (var entry in _state.Load())
        {
            // A day pass that ran out while the registry was down has run out.
            if (string.IsNullOrEmpty(entry.PlayerUid) || !entry.IsActiveAt(now)) { dropped = true; continue; }
            _entries[Key(entry.PlayerUid, entry.ServerId)] = entry;
        }
        if (dropped) Persist(now);
    }

    private static string Key(string playerUid, string? serverId)
        => (playerUid ?? "").ToLowerInvariant() + "|" + (serverId ?? "").ToLowerInvariant();

    // Adds or replaces the entry for this (uid, scope). Re-adding an already-listed player
    // updates the note and duration rather than stacking entries.
    public WhitelistEntry Add(WhitelistEntry entry)
    {
        _entries[Key(entry.PlayerUid, entry.ServerId)] = entry;
        Persist();
        return entry;
    }

    public bool Remove(string playerUid, string? serverId)
    {
        if (!_entries.TryRemove(Key(playerUid, serverId), out _)) return false;
        // Write-through on the removal too, or somebody taken off the list walks back onto it
        // at the next restart.
        Persist();
        return true;
    }

    // The entry covering this player on `serverId`, or null. Pass an empty serverId to ask only
    // about network-wide coverage. Expired entries are skipped.
    public WhitelistEntry? FindCovering(string playerUid, string? serverId = null)
    {
        if (string.IsNullOrEmpty(playerUid)) return null;
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        foreach (var kv in _entries)
        {
            var entry = kv.Value;
            if (!string.Equals(entry.PlayerUid, playerUid, StringComparison.OrdinalIgnoreCase)) continue;
            if (!entry.IsActiveAt(now)) continue;
            if (entry.Covers(serverId)) return entry;
        }
        return null;
    }

    public List<WhitelistEntry> Active()
    {
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        var list = new List<WhitelistEntry>();
        foreach (var kv in _entries)
            if (kv.Value.IsActiveAt(now)) list.Add(kv.Value);
        return list;
    }

    public int Prune()
    {
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        int dropped = 0;
        foreach (var kv in _entries)
        {
            if (!kv.Value.IsActiveAt(now) && _entries.TryRemove(kv.Key, out _))
                dropped++;
        }
        if (dropped > 0) Persist(now);
        return dropped;
    }

    private void Persist() => Persist(_clock.GetUtcNow().ToUnixTimeSeconds());

    private void Persist(long nowUnix) => _state?.Save(_entries.Values, nowUnix);
}
