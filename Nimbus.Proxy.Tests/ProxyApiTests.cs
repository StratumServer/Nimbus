using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The surface every plugin is handed. It is the only way a plugin reaches a player or a
/// backend, so a lookup that misses is a moderation plugin that cannot find the person it was
/// told to move and a routing plugin that cannot find where to move them to.
///
/// Backed by a real ProxyListener holding real sessions, because that is what the lookups walk.
/// </summary>
public class ProxyApiTests
{
    [Fact]
    public async Task Players_AreTheSessionsTheProxyIsHolding()
    {
        await using var harness = await AdminHarness.StartAsync();
        var api = new ProxyApi(harness.Proxy);
        await harness.JoinAsync("uid-1", "alice");
        await harness.JoinAsync("uid-2", "bob");

        var names = api.Players.Select(p => p.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "alice", "bob" }, names);
    }

    [Fact]
    public async Task APlayerCanBeFoundByUid()
    {
        await using var harness = await AdminHarness.StartAsync();
        var api = new ProxyApi(harness.Proxy);
        await harness.JoinAsync("uid-1", "alice");

        var found = api.FindPlayerByUid("uid-1");

        Assert.Equal("alice", found?.Name);
    }

    [Fact]
    public async Task ThePlayerUidLookup_IgnoresCase()
    {
        await using var harness = await AdminHarness.StartAsync();
        var api = new ProxyApi(harness.Proxy);
        await harness.JoinAsync("UID-Mixed", "alice");

        // Uids come off the wire and out of plugin config; a plugin that stored one in a
        // different case must still find the player.
        Assert.NotNull(api.FindPlayerByUid("uid-mixed"));
    }

    [Fact]
    public async Task APlayerCanBeFoundByName()
    {
        await using var harness = await AdminHarness.StartAsync();
        var api = new ProxyApi(harness.Proxy);
        await harness.JoinAsync("uid-1", "Alice");

        // Names are what a plugin's own commands are typed with, so the lookup has to forgive
        // the case the way the game's chat does.
        Assert.Equal("uid-1", api.FindPlayerByName("alice")?.Uid);
    }

    [Fact]
    public async Task LookingForSomebodyWhoIsNotOnline_ComesBackEmptyRatherThanThrowing()
    {
        await using var harness = await AdminHarness.StartAsync();
        var api = new ProxyApi(harness.Proxy);
        await harness.JoinAsync("uid-1", "alice");

        Assert.Null(api.FindPlayerByUid("uid-nobody"));
        Assert.Null(api.FindPlayerByName("nobody"));
    }

    [Fact]
    public async Task APlayerCanBeFetchedByTheSessionIdAnOperatorSees()
    {
        await using var harness = await AdminHarness.StartAsync();
        var api = new ProxyApi(harness.Proxy);
        var player = await harness.JoinAsync("uid-1", "alice");

        Assert.True(api.TryGetPlayer(player.Id, out var found));
        Assert.Equal("alice", found.Name);
        Assert.False(api.TryGetPlayer(999, out _));
    }

    [Fact]
    public async Task ABackendCanBeResolvedThroughTheRegistryByItsId()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.Backends["creative"] = new BackendSnapshot
        {
            ServerId = "creative", PublicHost = "10.0.0.9", PublicPort = 42430,
        };
        var api = new ProxyApi(harness.Proxy);

        var server = await api.ResolveServerAsync("creative", CancellationToken.None);

        // The registry's address is the live one, which is the whole point of resolving through
        // it rather than off the config file.
        Assert.Equal("10.0.0.9", server?.Host);
        Assert.Equal(42430, server?.Port);
    }

    [Fact]
    public async Task ABackendTheRegistryDoesNotKnow_FallsBackToTheConfiguredPool()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        var api = new ProxyApi(harness.Proxy);

        var server = await api.ResolveServerAsync("creative", CancellationToken.None);

        // Plugins have to keep working on networks with the registry off, and on backends the
        // registry has not heard from yet.
        Assert.Equal("creative", server?.ServerId);
        Assert.Equal("127.0.0.1", server?.Host);
    }

    [Fact]
    public async Task ABackendResolvedWithoutARegistryAtAll_StillComesFromTheConfig()
    {
        await using var harness = await AdminHarness.StartAsync(withRegistry: false, serverIds: new[] { "hub" });
        var api = new ProxyApi(harness.Proxy);

        Assert.Equal("hub", (await api.ResolveServerAsync("hub", CancellationToken.None))?.ServerId);
    }

    [Fact]
    public async Task TheBackendLookup_IgnoresCase()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub" });
        var api = new ProxyApi(harness.Proxy);

        Assert.NotNull(await api.ResolveServerAsync("HUB", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("typo")]
    public async Task ABackendThatIsNowhere_ComesBackNullRatherThanGuessing(string serverId)
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub" });
        var api = new ProxyApi(harness.Proxy);

        // Handing back the default backend here would send a plugin's players somewhere it never
        // asked for.
        Assert.Null(await api.ResolveServerAsync(serverId, CancellationToken.None));
    }

    [Fact]
    public async Task TheEventBusAPluginGets_IsTheOneTheSessionsFireOn()
    {
        await using var harness = await AdminHarness.StartAsync();
        var api = new ProxyApi(harness.Proxy);

        // A plugin subscribing on a bus nothing publishes to is a plugin that silently never
        // runs, which is the hardest kind of failure to notice.
        Assert.Same(harness.Proxy.Events, api.Events);
    }
}
