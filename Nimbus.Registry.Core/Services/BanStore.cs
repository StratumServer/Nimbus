using System.Collections.Concurrent;
using Nimbus.Shared.Models;

namespace Nimbus.Registry.Services;

// Network ban list. One entry per (PlayerUid, ServerId) pair, so a player can hold a
// network-wide ban and per-backend bans at the same time. Timed bans expire on read and are
// dropped by the background sweep.
public sealed class BanStore
{
    private readonly ConcurrentDictionary<string, NetworkBan> _bans = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public BanStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    private static string Key(string playerUid, string? serverId)
        => (playerUid ?? "").ToLowerInvariant() + "|" + (serverId ?? "").ToLowerInvariant();

    // Adds or replaces the ban for this (uid, scope). Re-banning an already-banned player
    // updates the reason and duration rather than stacking entries.
    public NetworkBan Add(NetworkBan ban)
    {
        _bans[Key(ban.PlayerUid, ban.ServerId)] = ban;
        return ban;
    }

    public bool Lift(string playerUid, string? serverId)
        => _bans.TryRemove(Key(playerUid, serverId), out _);

    // Every ban that currently blocks this player from `serverId`. Pass an empty serverId to
    // ask only about network-wide bans (the connection gate). Expired entries are skipped.
    public NetworkBan? FindBlocking(string playerUid, string? serverId = null)
    {
        if (string.IsNullOrEmpty(playerUid)) return null;
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        foreach (var kv in _bans)
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
        var list = new List<NetworkBan>();
        foreach (var kv in _bans)
            if (kv.Value.IsActiveAt(now)) list.Add(kv.Value);
        return list;
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
        return dropped;
    }
}
