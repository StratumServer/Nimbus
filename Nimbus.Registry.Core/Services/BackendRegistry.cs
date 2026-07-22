using System.Collections.Concurrent;
using Nimbus.Shared;
using Nimbus.Shared.Models;

namespace Nimbus.Registry.Services;

// Currently-known backends. Updated on each heartbeat, pruned by a background sweep, queried
// by /api/servers and reservation minting.
public sealed class BackendRegistry
{
    private readonly ConcurrentDictionary<string, BackendRecord> _backends = new(StringComparer.OrdinalIgnoreCase);
    private readonly RegistryConfig _cfg;
    private readonly TimeProvider _clock;

    public BackendRegistry(RegistryConfig cfg, TimeProvider? clock = null)
    {
        _cfg = cfg;
        _clock = clock ?? TimeProvider.System;
    }

    public void Upsert(BackendHeartbeat hb)
    {
        var rec = new BackendRecord
        {
            Heartbeat = hb,
            LastSeenUnix = _clock.GetUtcNow().ToUnixTimeSeconds()
        };
        _backends[hb.ServerId] = rec;
    }

    public BackendRecord? Get(string serverId)
        => _backends.TryGetValue(serverId, out var r) ? r : null;

    public NetworkSnapshot Snapshot()
    {
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        var snap = new NetworkSnapshot { GeneratedAtUnix = now };
        foreach (var rec in _backends.Values)
        {
            var hb = rec.Heartbeat;
            bool stale = (now - rec.LastSeenUnix) > _cfg.BackendStaleSeconds;
            snap.Backends.Add(new BackendSnapshot
            {
                ServerId = hb.ServerId,
                DisplayName = hb.DisplayName,
                PublicHost = hb.PublicHost,
                PublicPort = hb.PublicPort,
                Tags = hb.Tags,
                Players = hb.Players,
                MaxPlayers = hb.MaxPlayers,
                Tps = hb.Tps,
                Maintenance = hb.Maintenance,
                ReservationRequired = hb.ReservationRequired,
                LastSeenUnix = rec.LastSeenUnix,
                Stale = stale,
                GameVersion = hb.GameVersion,
                RequiredClientMods = hb.RequiredClientMods
            });
            if (!stale)
            {
                snap.TotalPlayers += hb.Players;
                snap.TotalCapacity += hb.MaxPlayers;
            }
        }
        return snap;
    }

    // Drop entries that haven't sent a heartbeat in RegistryConfig.BackendDropSeconds.
    public int Prune()
    {
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        int dropped = 0;
        foreach (var kv in _backends)
        {
            if ((now - kv.Value.LastSeenUnix) > _cfg.BackendDropSeconds
                && _backends.TryRemove(kv.Key, out _))
            {
                dropped++;
            }
        }
        return dropped;
    }
}

public sealed class BackendRecord
{
    public required BackendHeartbeat Heartbeat { get; init; }
    public required long LastSeenUnix { get; init; }
}
