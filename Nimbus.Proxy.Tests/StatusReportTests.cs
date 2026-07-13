using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

public class StatusReportTests
{
    private static ProxyConfig Config(params string[] serverIds)
    {
        var cfg = new ProxyConfig { Servers = new(), Try = new() };
        int port = 42421;
        foreach (string id in serverIds) cfg.Servers[id] = $"10.0.0.1:{port++}";
        cfg.Status.Name = "Test Network";
        return cfg;
    }

    private static Task<StatusReport> Build(ProxyConfig cfg, BackendRouter router, IRegistryClient? registry)
        => StatusReport.BuildAsync(cfg, router, registry,
            startUnix: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 42, CancellationToken.None);

    [Fact]
    public async Task MergesTheSnapshotHealth_IntoTheConfiguredPool()
    {
        var cfg = Config("hub", "silent");
        var registry = new FakeRegistryClient
        {
            Snapshot = new NetworkSnapshot
            {
                Backends = new()
                {
                    new BackendSnapshot
                    {
                        ServerId = "HUB", // ids match case-insensitively
                        Players = 7, MaxPlayers = 32,
                        ReservationRequired = true,
                        GameVersion = "1.22.0", LastSeenUnix = 1234,
                    },
                },
            },
        };
        var report = await Build(cfg, new BackendRouter(cfg, registry), registry);

        Assert.True(report.Ok);
        Assert.Equal("Test Network", report.Proxy.Name);
        Assert.InRange(report.Proxy.UptimeSeconds, 42, 45);

        var hub = report.Backends.Single(b => b.ServerId == "hub");
        Assert.True(hub.Registered);
        Assert.Equal(7, hub.Players);
        Assert.Equal(32, hub.MaxPlayers);
        Assert.True(hub.ReservationRequired);
        Assert.Equal("1.22.0", hub.GameVersion);
        Assert.Equal(1234, hub.LastSeenUnix);

        // Configured but never heartbeated: visible, flagged, health at defaults.
        var silent = report.Backends.Single(b => b.ServerId == "silent");
        Assert.False(silent.Registered);
        Assert.Equal(0, silent.MaxPlayers);

        Assert.Equal(7, report.Totals.Players);
        Assert.Equal(32, report.Totals.Capacity);
    }

    [Fact]
    public async Task StaleBackends_AreListed_ButExcludedFromTotals()
    {
        var cfg = Config("hub");
        var registry = new FakeRegistryClient
        {
            Snapshot = new NetworkSnapshot
            {
                Backends = new()
                {
                    new BackendSnapshot { ServerId = "hub", Players = 9, MaxPlayers = 32, Stale = true },
                },
            },
        };
        var report = await Build(cfg, new BackendRouter(cfg, registry), registry);

        var hub = Assert.Single(report.Backends);
        Assert.True(hub.Stale);
        Assert.Equal(9, hub.Players);
        Assert.Equal(0, report.Totals.Players);
        Assert.Equal(0, report.Totals.Capacity);
    }

    [Fact]
    public async Task DrainFlags_ComeFromTheRouter_NotTheRegistry()
    {
        var cfg = Config("hub", "spare");
        var router = new BackendRouter(cfg, registry: null);
        router.Drain("spare");

        var report = await Build(cfg, router, registry: null);

        Assert.False(report.Backends.Single(b => b.ServerId == "hub").Drained);
        Assert.True(report.Backends.Single(b => b.ServerId == "spare").Drained);
    }

    [Fact]
    public async Task WithoutARegistry_TheReportStillLists_TheConfiguredPool()
    {
        var cfg = Config("hub");
        var report = await Build(cfg, new BackendRouter(cfg, null), registry: null);

        var hub = Assert.Single(report.Backends);
        Assert.False(hub.Registered);
        Assert.Equal("10.0.0.1", hub.Host);
        Assert.Equal(42421, hub.Port);
    }

    [Fact]
    public async Task ARegistryFailure_DoesNotTakeTheStatusDown()
    {
        var cfg = Config("hub");
        var registry = new FakeRegistryClient { Throw = true };

        var report = await Build(cfg, new BackendRouter(cfg, registry), registry);

        Assert.True(report.Ok);
        Assert.False(Assert.Single(report.Backends).Registered);
    }
}
