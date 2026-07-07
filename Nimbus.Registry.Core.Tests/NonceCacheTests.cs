using Nimbus.Registry.Services;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

public class NonceCacheTests
{
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Fact]
    public void TryRecord_AcceptsFresh_RejectsReplay()
    {
        var cache = new NonceCache(new RegistryConfig());

        Assert.True(cache.TryRecord("nonce-1", Now));
        Assert.False(cache.TryRecord("nonce-1", Now));
        Assert.True(cache.TryRecord("nonce-2", Now));
    }

    [Fact]
    public void Prune_DropsNoncesOlderThanWindow_KeepsFresh()
    {
        var cfg = new RegistryConfig { NonceWindowSeconds = 90 };
        var cache = new NonceCache(cfg);
        cache.TryRecord("old", Now - cfg.NonceWindowSeconds - 10);
        cache.TryRecord("fresh", Now);

        Assert.Equal(1, cache.Prune());
        // The pruned nonce is accepted again (its replay window is over) and the fresh
        // one is still rejected.
        Assert.True(cache.TryRecord("old", Now));
        Assert.False(cache.TryRecord("fresh", Now));
    }
}
