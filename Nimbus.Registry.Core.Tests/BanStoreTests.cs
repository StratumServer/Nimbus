using Nimbus.Registry.Services;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

public class BanStoreTests
{
    private static NetworkBan Ban(string uid = "uid-1", string serverId = "", long expiresAt = 0)
        => new() { PlayerUid = uid, PlayerName = "alice", ServerId = serverId, ExpiresAtUnix = expiresAt };

    [Fact]
    public void NetworkWideBan_BlocksEveryBackendAndTheNetworkItself()
    {
        var store = new BanStore();
        store.Add(Ban());

        Assert.NotNull(store.FindBlocking("uid-1"));
        Assert.NotNull(store.FindBlocking("uid-1", "hub"));
        Assert.NotNull(store.FindBlocking("uid-1", "creative"));
    }

    [Fact]
    public void ScopedBan_BlocksOnlyItsOwnBackend()
    {
        var store = new BanStore();
        store.Add(Ban(serverId: "creative"));

        Assert.NotNull(store.FindBlocking("uid-1", "creative"));
        Assert.Null(store.FindBlocking("uid-1", "hub"));
        // The connection gate asks about the network, which a scoped ban must not block.
        Assert.Null(store.FindBlocking("uid-1"));
    }

    [Fact]
    public void UidMatching_IsCaseInsensitiveAndOtherPlayersAreUnaffected()
    {
        var store = new BanStore();
        store.Add(Ban(uid: "UID-1"));

        Assert.NotNull(store.FindBlocking("uid-1"));
        Assert.Null(store.FindBlocking("uid-2"));
    }

    [Fact]
    public void RebanningTheSameScope_ReplacesInsteadOfStacking()
    {
        var store = new BanStore();
        store.Add(Ban());
        store.Add(new NetworkBan { PlayerUid = "uid-1", Reason = "second thoughts" });

        var active = store.Active();
        Assert.Single(active);
        Assert.Equal("second thoughts", active[0].Reason);
    }

    [Fact]
    public void NetworkAndScopedBans_CoexistForTheSamePlayer()
    {
        var store = new BanStore();
        store.Add(Ban());
        store.Add(Ban(serverId: "creative"));

        Assert.Equal(2, store.Active().Count);
        Assert.True(store.Lift("uid-1", ""));
        // Lifting the network ban leaves the scoped one in place.
        Assert.Null(store.FindBlocking("uid-1"));
        Assert.NotNull(store.FindBlocking("uid-1", "creative"));
    }

    [Fact]
    public void TimedBan_StopsBlockingOnceItExpires()
    {
        var clock = new FakeClock();
        var store = new BanStore(clock);
        store.Add(Ban(expiresAt: clock.NowUnix + 60));

        Assert.NotNull(store.FindBlocking("uid-1"));

        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Null(store.FindBlocking("uid-1"));
        Assert.Empty(store.Active());
    }

    [Fact]
    public void Prune_DropsOnlyExpiredEntries()
    {
        var clock = new FakeClock();
        var store = new BanStore(clock);
        store.Add(Ban(uid: "temp", expiresAt: clock.NowUnix + 30));
        store.Add(Ban(uid: "forever"));

        Assert.Equal(0, store.Prune());

        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal(1, store.Prune());
        Assert.Single(store.Active());
        Assert.Equal(0, store.Prune());
    }

    [Fact]
    public void Lift_ReportsWhetherAnythingWasRemoved()
    {
        var store = new BanStore();
        store.Add(Ban());

        Assert.False(store.Lift("uid-1", "creative"));  // wrong scope
        Assert.False(store.Lift("uid-2", ""));          // wrong player
        Assert.True(store.Lift("uid-1", ""));
        Assert.False(store.Lift("uid-1", ""));          // already gone
    }
}
