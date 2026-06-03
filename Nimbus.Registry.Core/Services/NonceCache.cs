using System.Collections.Concurrent;

namespace Nimbus.Registry.Services;

// Tracks recently-seen request nonces to reject replays. Older than the configured window
// gets pruned on every sweep.
public sealed class NonceCache
{
    private readonly ConcurrentDictionary<string, long> _seen = new();
    private readonly RegistryConfig _cfg;

    public NonceCache(RegistryConfig cfg) { _cfg = cfg; }

    // Returns true if the nonce is fresh and was added, false if it's a replay.
    public bool TryRecord(string nonce, long nowUnix)
    {
        return _seen.TryAdd(nonce, nowUnix);
    }

    public int Prune()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int dropped = 0;
        foreach (var kv in _seen)
        {
            if ((now - kv.Value) > _cfg.NonceWindowSeconds && _seen.TryRemove(kv.Key, out _))
                dropped++;
        }
        return dropped;
    }
}
