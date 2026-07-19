using Nimbus.Registry.Services;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

public class TransferIntentStoreTests
{
    private readonly FakeClock clock = new();
    private TransferIntentStore NewStore() => new(clock);
    private long Now => clock.NowUnix;

    private static TransferIntentRequest Request(int ttl = 0, string? clientTransferId = null,
        string? mode = null) => new()
    {
        PlayerUid = "uid-1",
        PlayerName = "tester",
        SourceServerId = "hub",
        TargetServerId = "backend-1",
        TtlSeconds = ttl,
        ClientTransferId = clientTransferId,
        Mode = mode ?? "",
    };

    [Fact]
    public void Add_DefaultsTtlTo30_AndClampsTo300()
    {
        var store = NewStore();

        var defaulted = store.Add(Request(ttl: 0));
        var clamped = store.Add(Request(ttl: 9999));

        // The frozen clock makes the clamp exact, not a tolerance range.
        Assert.Equal(Now + 30, defaulted.ExpiresAtUnix);
        Assert.Equal(Now + 300, clamped.ExpiresAtUnix);
    }

    [Fact]
    public void Add_GeneratesId_OrPreservesClientTransferId()
    {
        var store = NewStore();

        var generated = store.Add(Request());
        var explicitId = store.Add(Request(clientTransferId: " transfer-42 "));

        Assert.False(string.IsNullOrWhiteSpace(generated.Id));
        Assert.Equal("transfer-42", explicitId.Id);
        Assert.Equal(explicitId.Id, explicitId.ClientTransferId);
    }

    [Fact]
    public void Add_DefaultsModeToRedirect()
    {
        var store = NewStore();

        Assert.Equal("redirect", store.Add(Request()).Mode);
        Assert.Equal("seamless", store.Add(Request(mode: "seamless")).Mode);
    }

    [Fact]
    public void Drain_DeliversAtMostOnce()
    {
        var store = NewStore();
        store.Add(Request(clientTransferId: "t-1"));
        store.Add(Request(clientTransferId: "t-2"));

        var first = store.Drain();
        var second = store.Drain();

        Assert.Equal(2, first.Count);
        Assert.Empty(second);
    }

    [Fact]
    public void Drain_DiscardsIntentsWhoseTtlElapsed()
    {
        var store = NewStore();
        store.Add(Request(ttl: 30, clientTransferId: "t-stale"));
        clock.Advance(TimeSpan.FromSeconds(31)); // the operator waited too long

        Assert.Empty(store.Drain());
    }
}
