using System.Collections.Concurrent;
using Nimbus.Shared.Models;

namespace Nimbus.Registry.Services;

// The mechanism behind the registry's per-scope lists. One entry per (PlayerUid, ServerId) pair,
// so a player can hold a network-wide entry and per-backend entries at the same time. Timed
// entries expire on read and are dropped by the background sweep.
//
// Given a state file, the list survives a restart: it is read once at construction and written
// back whole on every change. Without one the store is memory-only, which is what the tests and
// any caller that does not want a file on disk get.
//
// BanStore and WhitelistStore are thin faces over this. They keep the names their callers read
// (FindBlocking, FindCovering, Lift, Remove) and, more importantly, their own doc comments,
// because the reason each list persists differs even where the code does not.
internal sealed class RegistryEntryStore<TEntry> where TEntry : class, IScopedEntry
{
    private readonly ConcurrentDictionary<string, TEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly RegistryStateFile<TEntry>? _state;

    public RegistryEntryStore(TimeProvider? clock, RegistryStateFile<TEntry>? state)
    {
        _clock = clock ?? TimeProvider.System;
        _state = state;
        if (_state is null) return;

        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        bool dropped = false;
        foreach (var entry in _state.Load())
        {
            // An entry that ran out while the registry was down is over. Keeping it until the first
            // sweep would restore a state the clock has already retired: a served ban put back in
            // force, a lapsed pass still admitting.
            if (string.IsNullOrEmpty(entry.PlayerUid) || !entry.IsActiveAt(now)) { dropped = true; continue; }
            _entries[Key(entry.PlayerUid, entry.ServerId)] = entry;
        }
        // Written back only when the file and the list disagree, so an untouched registry does
        // not rewrite its state on every boot.
        if (dropped) Persist(now);
    }

    private static string Key(string playerUid, string? serverId)
        => (playerUid ?? "").ToLowerInvariant() + "|" + (serverId ?? "").ToLowerInvariant();

    // Adds or replaces the entry for this (uid, scope). Re-listing an already-listed player
    // updates the entry in place rather than stacking entries.
    public TEntry Add(TEntry entry)
    {
        _entries[Key(entry.PlayerUid, entry.ServerId)] = entry;
        Persist();
        return entry;
    }

    public bool Remove(string playerUid, string? serverId)
    {
        if (!_entries.TryRemove(Key(playerUid, serverId), out _)) return false;
        // Write-through on the removal too. A removal that only lived in memory would come back
        // from the file on the next restart, which is the failure nobody checks for.
        Persist();
        return true;
    }

    // The entry that currently applies to this player on `serverId`, or null. Pass an empty
    // serverId to ask only about network-wide entries (the connection gate). Expired entries are
    // skipped.
    public TEntry? Find(string playerUid, string? serverId = null)
    {
        if (string.IsNullOrEmpty(playerUid)) return null;
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        // Kept as a walk rather than the Where/FirstOrDefault the analyser asks for: the connection
        // gate calls this on every join, and the LINQ form either copies the whole table (.Values
        // snapshots a ConcurrentDictionary) or allocates a closure per call, where this exits on
        // the first match and allocates nothing. Active() below is the operator-driven listing and
        // does get the LINQ form.
        foreach (var kv in _entries) // NOSONAR
        {
            var entry = kv.Value;
            if (!string.Equals(entry.PlayerUid, playerUid, StringComparison.OrdinalIgnoreCase)) continue;
            if (!entry.IsActiveAt(now)) continue;
            if (entry.Matches(serverId)) return entry;
        }
        return null;
    }

    public List<TEntry> Active()
    {
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        return _entries.Values.Where(entry => entry.IsActiveAt(now)).ToList();
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
