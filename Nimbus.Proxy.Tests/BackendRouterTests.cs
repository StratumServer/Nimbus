using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

public class BackendRouterTests
{
    private static ProxyConfig Config(params string[] serverIds)
    {
        var cfg = new ProxyConfig { Servers = new(), Try = new() };
        int port = 42421;
        foreach (string id in serverIds) cfg.Servers[id] = $"10.0.0.1:{port++}";
        return cfg;
    }

    private static BackendSnapshot Healthy(string id, bool stale = false, bool maintenance = false,
        int players = 0, int maxPlayers = 32) => new()
    {
        ServerId = id,
        Stale = stale,
        Maintenance = maintenance,
        Players = players,
        MaxPlayers = maxPlayers,
    };

    private static NetworkSnapshot Snapshot(params BackendSnapshot[] backends)
        => new() { Backends = backends.ToList() };

    [Fact]
    public async Task WithoutARegistry_ConfiguredOrderPassesThrough()
    {
        var router = new BackendRouter(Config("alpha", "beta"), registry: null);

        var (ordered, reason) = await router.SelectOrderedAsync(CancellationToken.None);

        Assert.Null(reason);
        Assert.Equal(new[] { "alpha", "beta" }, ordered.Select(b => b.ServerId));
    }

    [Fact]
    public async Task TryList_DrivesTheOrder_AndSkipsUnknownNames()
    {
        var cfg = Config("alpha", "beta");
        cfg.Try = new() { "beta", "no-such-server", "ALPHA" }; // lookups are case-insensitive
        var router = new BackendRouter(cfg, registry: null);

        var (ordered, _) = await router.SelectOrderedAsync(CancellationToken.None);

        Assert.Equal(new[] { "beta", "alpha" }, ordered.Select(b => b.ServerId));
    }

    [Fact]
    public async Task Snapshot_FiltersStaleMaintenanceFullAndUnregistered()
    {
        var registry = new FakeRegistryClient
        {
            Snapshot = Snapshot(
                Healthy("stale-1", stale: true),
                Healthy("maint-1", maintenance: true),
                Healthy("full-1", players: 32, maxPlayers: 32),
                Healthy("ok-1")),
        };
        var router = new BackendRouter(Config("stale-1", "maint-1", "full-1", "ghost-1", "ok-1"), registry);

        var (ordered, reason) = await router.SelectOrderedAsync(CancellationToken.None);

        Assert.Null(reason);
        Assert.Equal(new[] { "ok-1" }, ordered.Select(b => b.ServerId));
    }

    [Fact]
    public async Task WhenEverythingIsSkipped_TheReasonNamesTheLastSkip()
    {
        var registry = new FakeRegistryClient { Snapshot = Snapshot(Healthy("only", maintenance: true)) };
        var router = new BackendRouter(Config("only"), registry);

        var (ordered, reason) = await router.SelectOrderedAsync(CancellationToken.None);

        Assert.Empty(ordered);
        Assert.Contains("maintenance", reason);
    }

    [Fact]
    public async Task EmptySnapshot_MeansNoHealthDataYet_EverythingPasses()
    {
        // A freshly-started embedded registry has no heartbeats yet; that must not
        // translate into "no viable candidates".
        var registry = new FakeRegistryClient { Snapshot = Snapshot() };
        var router = new BackendRouter(Config("alpha"), registry);

        var (ordered, _) = await router.SelectOrderedAsync(CancellationToken.None);

        Assert.Equal(new[] { "alpha" }, ordered.Select(b => b.ServerId));
    }

    [Fact]
    public async Task RegistryFailure_DegradesToConfiguredOrder()
    {
        var registry = new FakeRegistryClient { Throw = true };
        var router = new BackendRouter(Config("alpha", "beta"), registry);

        var (ordered, reason) = await router.SelectOrderedAsync(CancellationToken.None);

        Assert.Null(reason);
        Assert.Equal(2, ordered.Count);
    }

    [Fact]
    public async Task Drain_RemovesABackendFromRouting_UndrainRestoresIt()
    {
        var router = new BackendRouter(Config("alpha", "beta"), registry: null);

        Assert.True(router.Drain("alpha"));
        Assert.False(router.Drain("alpha"), "draining twice must report no change");
        Assert.True(router.IsDrained("ALPHA"), "drain lookups are case-insensitive");
        Assert.Equal(new[] { "alpha" }, router.ListDrained());

        var (ordered, _) = await router.SelectOrderedAsync(CancellationToken.None);
        Assert.Equal(new[] { "beta" }, ordered.Select(b => b.ServerId));

        Assert.True(router.Undrain("alpha"));
        (ordered, _) = await router.SelectOrderedAsync(CancellationToken.None);
        Assert.Equal(2, ordered.Count);
    }

    [Fact]
    public async Task SelectAsync_ReturnsTheFirstViableCandidate()
    {
        var registry = new FakeRegistryClient
        {
            Snapshot = Snapshot(Healthy("alpha", stale: true), Healthy("beta")),
        };
        var router = new BackendRouter(Config("alpha", "beta"), registry);

        var (target, reason) = await router.SelectAsync(CancellationToken.None);

        Assert.Null(reason);
        Assert.Equal("beta", target!.ServerId);
    }
}
