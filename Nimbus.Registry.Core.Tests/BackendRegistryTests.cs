using Nimbus.Registry.Services;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

public class BackendRegistryTests
{
    private static BackendHeartbeat Heartbeat(string id = "backend-1", int players = 3,
        int maxPlayers = 10, bool maintenance = false) => new()
    {
        ServerId = id,
        DisplayName = id,
        PublicHost = "10.0.0.1",
        PublicPort = 42421,
        Players = players,
        MaxPlayers = maxPlayers,
        Maintenance = maintenance,
    };

    [Fact]
    public void Snapshot_CountsFreshBackends()
    {
        var registry = new BackendRegistry(new RegistryConfig());
        registry.Upsert(Heartbeat("a", players: 3, maxPlayers: 10));
        registry.Upsert(Heartbeat("b", players: 2, maxPlayers: 20));

        var snap = registry.Snapshot();

        Assert.Equal(2, snap.Backends.Count);
        Assert.All(snap.Backends, b => Assert.False(b.Stale));
        Assert.Equal(5, snap.TotalPlayers);
        Assert.Equal(30, snap.TotalCapacity);
    }

    [Fact]
    public void Snapshot_MarksStale_AndExcludesFromCapacity()
    {
        // LastSeenUnix is stamped internally with the real clock, so force staleness
        // deterministically with a negative window instead of sleeping.
        var registry = new BackendRegistry(new RegistryConfig { BackendStaleSeconds = -1 });
        registry.Upsert(Heartbeat("a", players: 3, maxPlayers: 10));

        var snap = registry.Snapshot();

        Assert.True(Assert.Single(snap.Backends).Stale);
        Assert.Equal(0, snap.TotalPlayers);
        Assert.Equal(0, snap.TotalCapacity);
    }

    [Fact]
    public void Upsert_SameServerId_Replaces()
    {
        var registry = new BackendRegistry(new RegistryConfig());
        registry.Upsert(Heartbeat("a", players: 1));
        registry.Upsert(Heartbeat("A", players: 7)); // ids are case-insensitive

        var snap = registry.Snapshot();

        Assert.Single(snap.Backends);
        Assert.Equal(7, snap.TotalPlayers);
    }

    [Fact]
    public void Prune_DropsBackendsPastDropWindow()
    {
        var registry = new BackendRegistry(new RegistryConfig { BackendDropSeconds = -1 });
        registry.Upsert(Heartbeat("a"));

        Assert.Equal(1, registry.Prune());
        Assert.Null(registry.Get("a"));
        Assert.Empty(registry.Snapshot().Backends);
    }
}
