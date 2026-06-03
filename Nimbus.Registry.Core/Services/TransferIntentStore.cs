using System.Collections.Concurrent;
using System.Security.Cryptography;
using Nimbus.Shared.Models;

namespace Nimbus.Registry.Services;

// In-memory queue of transfer intents. Posted by backends, drained by the proxy. Drain is
// destructive: each intent is delivered at most once. If the proxy can't find a live session
// for the player (already disconnected, sitting on a different proxy instance, etc.) the
// intent is dropped and the operator has to re-issue.
public sealed class TransferIntentStore
{
    private readonly ConcurrentDictionary<string, TransferIntent> _intents = new();

    public TransferIntent Add(TransferIntentRequest req)
    {
        int ttl = req.TtlSeconds <= 0 ? 30 : Math.Min(req.TtlSeconds, 300);
        var intent = new TransferIntent
        {
            Id = NewId(),
            PlayerUid = req.PlayerUid,
            PlayerName = req.PlayerName ?? "",
            SourceServerId = req.SourceServerId ?? "",
            TargetServerId = req.TargetServerId,
            Mode = string.IsNullOrEmpty(req.Mode) ? "redirect" : req.Mode,
            Reason = req.Reason,
            ExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttl,
            RequestedBy = req.RequestedBy ?? "",
        };
        _intents[intent.Id] = intent;
        return intent;
    }

    // Remove and return all non-expired intents. Not a transactional snapshot: entries added
    // mid-drain may or may not show up in this batch, which is fine for our use.
    public List<TransferIntent> Drain()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var taken = new List<TransferIntent>();
        foreach (var kv in _intents)
        {
            if (!_intents.TryRemove(kv.Key, out var t)) continue;
            if (now > t.ExpiresAtUnix) continue;
            taken.Add(t);
        }
        return taken;
    }

    public int Prune()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int dropped = 0;
        foreach (var kv in _intents)
        {
            if (now > kv.Value.ExpiresAtUnix && _intents.TryRemove(kv.Key, out _))
                dropped++;
        }
        return dropped;
    }

    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }
}
