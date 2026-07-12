using Nimbus.Registry.Services;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

public class ReservationStoreTests
{
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static TransferReservation Make(string id = "res-1", string uid = "uid-1",
        string target = "backend-1", long? expiresAt = null) => new()
    {
        Id = id,
        PlayerUid = uid,
        PlayerName = "tester",
        SourceServerId = "hub",
        TargetServerId = target,
        ExpiresAtUnix = expiresAt ?? Now + 60,
    };

    [Fact]
    public void Consume_IsSingleUse()
    {
        var store = new ReservationStore();
        store.Add(Make());

        Assert.NotNull(store.Consume("res-1", "backend-1"));
        Assert.Null(store.Consume("res-1", "backend-1"));
    }

    [Fact]
    public void Consume_WrongTarget_ReturnsNull_AndKeepsTheReservation()
    {
        var store = new ReservationStore();
        store.Add(Make());

        Assert.Null(store.Consume("res-1", "other-backend"));
        // A mismatched target must not burn the reservation for the legitimate backend.
        Assert.NotNull(store.Peek("res-1"));
        Assert.NotNull(store.Consume("res-1", "BACKEND-1")); // target match is case-insensitive
    }

    [Fact]
    public void Consume_Expired_ReturnsNull_AndRemoves()
    {
        var store = new ReservationStore();
        store.Add(Make(expiresAt: Now - 1));

        Assert.Null(store.Consume("res-1", "backend-1"));
        Assert.Null(store.Peek("res-1"));
    }

    [Fact]
    public void ConsumeByUid_MatchesUidAndTarget_SingleUse()
    {
        var store = new ReservationStore();
        store.Add(Make());

        Assert.Null(store.ConsumeByUid("uid-1", "other-backend"));
        Assert.Null(store.ConsumeByUid("other-uid", "backend-1"));

        var taken = store.ConsumeByUid("UID-1", "backend-1"); // uid match is case-insensitive
        Assert.NotNull(taken);
        Assert.Equal("res-1", taken!.Id);
        Assert.Null(store.ConsumeByUid("uid-1", "backend-1"));
    }

    [Fact]
    public void ConsumeByUid_SkipsExpired_TakesValid()
    {
        var store = new ReservationStore();
        store.Add(Make(id: "res-old", expiresAt: Now - 1));
        store.Add(Make(id: "res-new"));

        var taken = store.ConsumeByUid("uid-1", "backend-1");

        Assert.NotNull(taken);
        Assert.Equal("res-new", taken!.Id);
    }

    [Fact]
    public void Prune_DropsOnlyExpired()
    {
        var store = new ReservationStore();
        store.Add(Make(id: "res-old", expiresAt: Now - 1));
        store.Add(Make(id: "res-new"));

        Assert.Equal(1, store.Prune());
        Assert.Null(store.Peek("res-old"));
        Assert.NotNull(store.Peek("res-new"));
    }
}
