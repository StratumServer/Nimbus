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

    public BackendRegistry(RegistryConfig cfg)
    {
        _cfg = cfg;
    }

    public void Upsert(BackendHeartbeat hb)
    {
        var rec = new BackendRecord
        {
            Heartbeat = hb,
            LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        _backends[hb.ServerId] = rec;
    }

    public BackendRecord? Get(string serverId)
        => _backends.TryGetValue(serverId, out var r) ? r : null;

    public NetworkSnapshot Snapshot()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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
                StratumVersion = hb.StratumVersion,
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
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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
