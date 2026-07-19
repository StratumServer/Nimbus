using Nimbus.Registry.Services;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

public class BackendRegistryTests
{
    private readonly FakeClock clock = new();

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
        var registry = new BackendRegistry(new RegistryConfig(), clock);
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
        // The injected clock makes the stale window testable for real: heartbeat at t0,
        // then let more than BackendStaleSeconds elapse.
        var cfg = new RegistryConfig { BackendStaleSeconds = 20 };
        var registry = new BackendRegistry(cfg, clock);
        registry.Upsert(Heartbeat("a", players: 3, maxPlayers: 10));
        clock.Advance(TimeSpan.FromSeconds(cfg.BackendStaleSeconds + 1));

        var snap = registry.Snapshot();

        Assert.True(Assert.Single(snap.Backends).Stale);
        Assert.Equal(0, snap.TotalPlayers);
        Assert.Equal(0, snap.TotalCapacity);
    }

    [Fact]
    public void Upsert_SameServerId_Replaces()
    {
        var registry = new BackendRegistry(new RegistryConfig(), clock);
        registry.Upsert(Heartbeat("a", players: 1));
        registry.Upsert(Heartbeat("A", players: 7)); // ids are case-insensitive

        var snap = registry.Snapshot();

        Assert.Single(snap.Backends);
        Assert.Equal(7, snap.TotalPlayers);
    }

    [Fact]
    public void Prune_DropsBackendsPastDropWindow()
    {
        var cfg = new RegistryConfig { BackendDropSeconds = 120 };
        var registry = new BackendRegistry(cfg, clock);
        registry.Upsert(Heartbeat("a"));
        clock.Advance(TimeSpan.FromSeconds(cfg.BackendDropSeconds + 1));

        Assert.Equal(1, registry.Prune());
        Assert.Null(registry.Get("a"));
        Assert.Empty(registry.Snapshot().Backends);
    }
}
