using Nimbus.Registry.Services;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

public class TransferIntentStoreTests
{
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

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
        var store = new TransferIntentStore();

        var defaulted = store.Add(Request(ttl: 0));
        var clamped = store.Add(Request(ttl: 9999));

        Assert.InRange(defaulted.ExpiresAtUnix, Now + 28, Now + 30);
        Assert.InRange(clamped.ExpiresAtUnix, Now + 298, Now + 300);
    }

    [Fact]
    public void Add_GeneratesId_OrPreservesClientTransferId()
    {
        var store = new TransferIntentStore();

        var generated = store.Add(Request());
        var explicitId = store.Add(Request(clientTransferId: " transfer-42 "));

        Assert.False(string.IsNullOrWhiteSpace(generated.Id));
        Assert.Equal("transfer-42", explicitId.Id);
        Assert.Equal(explicitId.Id, explicitId.ClientTransferId);
    }

    [Fact]
    public void Add_DefaultsModeToRedirect()
    {
        var store = new TransferIntentStore();

        Assert.Equal("redirect", store.Add(Request()).Mode);
        Assert.Equal("seamless", store.Add(Request(mode: "seamless")).Mode);
    }

    [Fact]
    public void Drain_DeliversAtMostOnce()
    {
        var store = new TransferIntentStore();
        store.Add(Request(clientTransferId: "t-1"));
        store.Add(Request(clientTransferId: "t-2"));

        var first = store.Drain();
        var second = store.Drain();

        Assert.Equal(2, first.Count);
        Assert.Empty(second);
    }
}
