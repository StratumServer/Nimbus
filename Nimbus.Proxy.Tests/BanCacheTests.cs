using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

public class BanCacheTests
{
    private static NetworkBan Ban(string uid = "uid-1", string serverId = "", long expiresAt = 0)
        => new() { PlayerUid = uid, PlayerName = "alice", ServerId = serverId, ExpiresAtUnix = expiresAt };

    private static BanCache Cache(FakeRegistryClient registry, TimeProvider? clock = null)
        => new(registry, CancellationToken.None, clock: clock);

    [Fact]
    public async Task BeforeAnyRefresh_NothingIsBlocked()
    {
        var cache = Cache(new FakeRegistryClient { Bans = new List<NetworkBan> { Ban() } });

        // The list is only consulted after a refresh; a cold cache must not block anyone.
        Assert.Null(cache.FindBlocking("uid-1"));

        await cache.RefreshAsync();

        Assert.NotNull(cache.FindBlocking("uid-1"));
    }

    [Fact]
    public async Task NetworkWideBan_BlocksTheNetworkAndEveryBackend()
    {
        var cache = Cache(new FakeRegistryClient { Bans = new List<NetworkBan> { Ban() } });
        await cache.RefreshAsync();

        Assert.NotNull(cache.FindBlocking("uid-1"));
        Assert.NotNull(cache.FindBlocking("uid-1", "hub"));
    }

    [Fact]
    public async Task ScopedBan_DoesNotBlockTheConnectionGate()
    {
        var cache = Cache(new FakeRegistryClient { Bans = new List<NetworkBan> { Ban(serverId: "creative") } });
        await cache.RefreshAsync();

        Assert.Null(cache.FindBlocking("uid-1"));
        Assert.NotNull(cache.FindBlocking("uid-1", "creative"));
        Assert.Null(cache.FindBlocking("uid-1", "hub"));
    }

    [Fact]
    public async Task RegistryError_KeepsTheLastKnownList()
    {
        var registry = new FakeRegistryClient { Bans = new List<NetworkBan> { Ban() } };
        var cache = Cache(registry);
        await cache.RefreshAsync();

        // Registry unreachable: bans must keep applying rather than silently opening the gate.
        registry.Bans = null;
        await cache.RefreshAsync();

        Assert.NotNull(cache.FindBlocking("uid-1"));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task LiftedBan_StopsBlockingAfterTheNextRefresh()
    {
        var registry = new FakeRegistryClient { Bans = new List<NetworkBan> { Ban() } };
        var cache = Cache(registry);
        await cache.RefreshAsync();

        registry.Bans = new List<NetworkBan>();
        await cache.RefreshAsync();

        Assert.Null(cache.FindBlocking("uid-1"));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task TimedBan_ExpiresWithoutWaitingForARefresh()
    {
        var clock = new FakeCacheClock();
        var registry = new FakeRegistryClient
        {
            Bans = new List<NetworkBan> { Ban(expiresAt: clock.GetUtcNow().ToUnixTimeSeconds() + 60) },
        };
        var cache = Cache(registry, clock);
        await cache.RefreshAsync();

        Assert.NotNull(cache.FindBlocking("uid-1"));

        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Null(cache.FindBlocking("uid-1"));
    }

    [Fact]
    public async Task UnknownPlayersAndEmptyUids_AreNeverBlocked()
    {
        var cache = Cache(new FakeRegistryClient { Bans = new List<NetworkBan> { Ban() } });
        await cache.RefreshAsync();

        Assert.Null(cache.FindBlocking("uid-2"));
        Assert.Null(cache.FindBlocking(""));
        Assert.Null(cache.FindBlocking(null));
    }

    [Fact]
    public async Task WithoutARegistry_RefreshIsANoOp()
    {
        var cache = new BanCache(registry: null, CancellationToken.None);

        await cache.RefreshAsync();

        Assert.Equal(0, cache.Count);
        Assert.Null(cache.FindBlocking("uid-1"));
    }
}

/// <summary>Deterministic clock for the expiry test; the registry test project has its own.</summary>
internal sealed class FakeCacheClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}
