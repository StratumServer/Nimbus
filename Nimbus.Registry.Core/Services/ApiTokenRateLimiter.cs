using System.Collections.Concurrent;

namespace Nimbus.Registry.Services;

// Per-token token bucket. Per token and not only per IP, because a compromised bot credential
// with `whitelist:write` can do a lot of damage quickly from an address that has done nothing
// wrong, and because the addresses a hosted bot calls from are not something an operator can
// enumerate in advance.
//
// A bucket per token id, filled at the configured rate and capped at it, so a caller may burst
// up to a minute's worth and then settles to the steady rate. Buckets are kept for as long as the
// registry runs: there is one per issued token, which is a handful, not one per caller address.
public sealed class ApiTokenRateLimiter
{
    // What a non-positive configured limit falls back to. Refusing everything would turn a config
    // typo into an outage for every integration, and allowing everything would silently drop the
    // protection Zaldaryon asked for; the default is the only reading that does neither.
    // ProxyConfigValidator says so out loud rather than leaving it to be discovered here.
    public const int DefaultPerMinute = 60;

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly double _perMinute;

    public ApiTokenRateLimiter(RegistryConfig cfg, TimeProvider? clock = null)
        : this(cfg.ApiTokens.RateLimitPerMinute, clock)
    {
    }

    public ApiTokenRateLimiter(int perMinute, TimeProvider? clock = null)
    {
        _perMinute = perMinute > 0 ? perMinute : DefaultPerMinute;
        _clock = clock ?? TimeProvider.System;
    }

    // True when this call is within budget. On a refusal, retryAfterSeconds is how long the
    // caller has to wait for the next whole permit, never zero: a Retry-After of 0 invites the
    // immediate retry that caused the 429.
    public bool TryTake(string tokenId, out int retryAfterSeconds)
    {
        double perSecond = _perMinute / 60.0;
        var now = _clock.GetUtcNow();
        // A token seen for the first time starts full, so the first call an integration makes is
        // never the one that gets a 429.
        var bucket = _buckets.GetOrAdd(tokenId, _ => new Bucket(_perMinute, now));

        lock (bucket)
        {
            double elapsed = (now - bucket.LastRefill).TotalSeconds;
            // A clock that went backwards must not hand out credit, so only forward time refills.
            if (elapsed > 0)
            {
                bucket.Permits = Math.Min(_perMinute, bucket.Permits + elapsed * perSecond);
                bucket.LastRefill = now;
            }

            if (bucket.Permits >= 1.0)
            {
                bucket.Permits -= 1.0;
                retryAfterSeconds = 0;
                return true;
            }

            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((1.0 - bucket.Permits) / perSecond));
            return false;
        }
    }

    private sealed class Bucket
    {
        public Bucket(double permits, DateTimeOffset now)
        {
            Permits = permits;
            LastRefill = now;
        }

        public double Permits;
        public DateTimeOffset LastRefill;
    }
}
