using System.Collections.Concurrent;
using Nimbus.Shared.Models;

namespace Nimbus.Registry.Services;

// Short-lived single-use transfer reservations. Consumed (removed) on the first successful
// validation by a backend. Background sweep drops expired entries.
public sealed class ReservationStore
{
    private readonly ConcurrentDictionary<string, TransferReservation> _reservations = new();
    private readonly TimeProvider _clock;

    public ReservationStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public void Add(TransferReservation r) => _reservations[r.Id] = r;

    // Validate and consume a reservation. Returns it if it exists, isn't expired, and matches
    // the requested target server. Removed atomically on success.
    public TransferReservation? Consume(string id, string targetServerId)
    {
        if (!_reservations.TryGetValue(id, out var r)) return null;
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        if (now > r.ExpiresAtUnix) { _reservations.TryRemove(id, out _); return null; }
        if (!string.Equals(r.TargetServerId, targetServerId, StringComparison.OrdinalIgnoreCase)) return null;
        return _reservations.TryRemove(id, out var taken) ? taken : null;
    }

    public TransferReservation? Peek(string id)
        => _reservations.TryGetValue(id, out var r) ? r : null;

    // Consume any non-expired reservation that matches (PlayerUid, TargetServerId). Used by
    // the target backend during identification when the player arrives without an explicit
    // reservation id (vanilla clients have no way to carry one).
    public TransferReservation? ConsumeByUid(string playerUid, string targetServerId)
    {
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        foreach (var kv in _reservations)
        {
            var r = kv.Value;
            if (now > r.ExpiresAtUnix) continue;
            if (!string.Equals(r.PlayerUid, playerUid, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(r.TargetServerId, targetServerId, StringComparison.OrdinalIgnoreCase)) continue;
            if (_reservations.TryRemove(kv.Key, out var taken)) return taken;
        }
        return null;
    }

    public int Prune()
    {
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        int dropped = 0;
        foreach (var kv in _reservations)
        {
            if (now > kv.Value.ExpiresAtUnix && _reservations.TryRemove(kv.Key, out _))
                dropped++;
        }
        return dropped;
    }
}
