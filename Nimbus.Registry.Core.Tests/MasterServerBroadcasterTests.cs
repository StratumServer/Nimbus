using System.Text.Json;
using Nimbus.Registry.MasterServer;
using Nimbus.Registry.Services;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

/// <summary>
/// The broadcaster that puts a Nimbus network on the Vintage Story server list, driven against a
/// stand-in master server on a loopback port. Nothing is stubbed between the two: the real HTTP
/// client posts the real packets and the assertions read the JSON that came off the wire.
///
/// What a player sees of all this is a server list entry. An entry with the wrong port or the
/// wrong mod list is one they cannot join from, an entry advertising zero slots is one they will
/// not click, and an entry left behind after shutdown is one that times them out.
/// </summary>
public class MasterServerBroadcasterTests
{
    private static BackendHeartbeat Backend(string id, int maxPlayers,
        params (string Id, string Version)[] mods) => new()
        {
            ServerId = id,
            DisplayName = id,
            PublicHost = "10.0.0.1",
            PublicPort = 42421,
            Players = 0,
            MaxPlayers = maxPlayers,
            RequiredClientMods = mods.Select(m => new BackendModInfo { Id = m.Id, Version = m.Version }).ToArray(),
        };

    private static RegistryConfig Advertising(string masterUrl, Action<ServerIdentityConfig>? configure = null)
    {
        var cfg = new RegistryConfig();
        cfg.Identity.AdvertiseOnMasterServer = true;
        cfg.Identity.MasterServerUrl = masterUrl;
        cfg.Identity.PublicHost = "play.example.net";
        configure?.Invoke(cfg.Identity);
        return cfg;
    }

    /// <summary>Runs the broadcaster over <paramref name="body"/> and stops it cleanly, which is
    /// what triggers the unregister. The body is handed the broadcaster's log, because a request
    /// arriving at the master server is not the same as the broadcaster having read the answer to
    /// it, and a test that stops in between sees neither.</summary>
    private static async Task<RecordingLogger> RunAsync(RegistryConfig cfg, BackendRegistry backends,
        Func<RecordingLogger, Task> body)
    {
        var log = new RecordingLogger();
        var broadcaster = new MasterServerBroadcaster(cfg, backends, log);
        await broadcaster.StartAsync(CancellationToken.None);
        try { await body(log); }
        finally { await broadcaster.StopAsync(CancellationToken.None); }
        // A background service that faulted would leave the registry running with no advertising
        // and nothing said about it, so the task itself is part of the contract.
        Assert.False(broadcaster.ExecuteTask?.IsFaulted, broadcaster.ExecuteTask?.Exception?.ToString());
        return log;
    }

    private static BackendRegistry RegistryWith(params BackendHeartbeat[] backends)
    {
        var registry = new BackendRegistry(new RegistryConfig());
        foreach (var b in backends) registry.Upsert(b);
        return registry;
    }

    private static string[] ModIds(JsonElement packet)
        => packet.GetProperty("Mods").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToArray();

    // ---- the kill switches ----

    [Fact]
    public async Task WithAdvertisingOff_TheMasterServerIsNeverContacted()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = new RegistryConfig();
        cfg.Identity.MasterServerUrl = master.Url;

        // Off by default, and off has to mean off: a private network must not appear on a public
        // server list because someone filled in the identity block.
        await RunAsync(cfg, RegistryWith(Backend("hub", 10)), async _ => await Task.Delay(300));

        Assert.Empty(master.Calls);
    }

    [Fact]
    public async Task WithAdvertisingOnButNoPublicHost_NothingIsAdvertised()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url, id => id.PublicHost = "");

        // Registering without a reachable host would publish an entry nobody can connect to.
        await RunAsync(cfg, RegistryWith(Backend("hub", 10)), async _ => await Task.Delay(300));

        Assert.Empty(master.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankPublicHost_CountsAsNoneAtAll(string host)
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url, id => id.PublicHost = host);

        await RunAsync(cfg, RegistryWith(Backend("hub", 10)), async _ => await Task.Delay(300));

        Assert.Empty(master.Calls);
    }

    // ---- the register packet ----

    [Fact]
    public async Task TheRegisterPacket_CarriesTheNetworkIdentityAsConfigured()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url, id =>
        {
            id.PublicPort = 42999;
            id.ServerName = "Stratum Network";
            id.ServerDescription = "four worlds, one login";
            id.ServerUrl = "https://stratum.example";
            id.ServerIcon = "icon-data";
            id.VhIdentifier = "vh-1234";
            id.GameVersion = "1.22.6";
            id.HasPassword = true;
            id.Whitelisted = true;
            id.Playstyle = new PlaystyleConfig { Id = "wilderness", LangCode = "preset-wilderness" };
        });

        JsonElement packet = default;
        await RunAsync(cfg, RegistryWith(Backend("hub", 40)),
            async _ => packet = await master.WaitForAsync("register"));

        // The port is the proxy's, never a backend's: it is what the client dials from the list.
        Assert.Equal(42999, packet.GetProperty("port").GetInt32());
        Assert.Equal("Stratum Network", packet.GetProperty("name").GetString());
        Assert.Equal("four worlds, one login", packet.GetProperty("gameDescription").GetString());
        Assert.Equal("https://stratum.example", packet.GetProperty("serverUrl").GetString());
        Assert.Equal("icon-data", packet.GetProperty("icon").GetString());
        Assert.Equal("vh-1234", packet.GetProperty("vhIdentifier").GetString());
        Assert.Equal("1.22.6", packet.GetProperty("gameVersion").GetString());
        Assert.True(packet.GetProperty("hasPassword").GetBoolean());
        Assert.True(packet.GetProperty("whitelisted").GetBoolean());
        Assert.Equal("wilderness", packet.GetProperty("playstyle").GetProperty("id").GetString());
        Assert.Equal("preset-wilderness", packet.GetProperty("playstyle").GetProperty("langCode").GetString());
    }

    [Fact]
    public async Task TheAdvertisedCapacity_IsTheSumOfTheLiveBackends()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url);

        JsonElement packet = default;
        await RunAsync(cfg, RegistryWith(Backend("hub", 10), Backend("creative", 20)),
            async _ => packet = await master.WaitForAsync("register"));

        // The network is one entry on the list, so its slot count is the whole network's.
        Assert.Equal(30, packet.GetProperty("maxPlayers").GetInt32());
    }

    [Fact]
    public async Task AStaleBackend_IsNotCountedInTheAdvertisedCapacity()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url);

        var clock = new FakeClock();
        var registryCfg = new RegistryConfig { BackendStaleSeconds = 20 };
        var backends = new BackendRegistry(registryCfg, clock);
        backends.Upsert(Backend("gone", 50));
        clock.Advance(TimeSpan.FromSeconds(60));
        backends.Upsert(Backend("hub", 10));

        JsonElement packet = default;
        await RunAsync(cfg, backends, async _ => packet = await master.WaitForAsync("register"));

        // A backend that stopped answering has no slots to offer. Advertising them sends players
        // at a server that is not there.
        Assert.Equal(10, packet.GetProperty("maxPlayers").GetInt32());
    }

    [Fact]
    public async Task AnExplicitMaxPlayers_WinsOverWhatTheBackendsReport()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url, id => id.MaxPlayersOverride = 64);

        JsonElement packet = default;
        await RunAsync(cfg, RegistryWith(Backend("hub", 10)),
            async _ => packet = await master.WaitForAsync("register"));

        // An operator running four 20-slot backends does not have 80 concurrent seats worth of
        // hardware, so the override is what goes on the list.
        Assert.Equal(64, packet.GetProperty("maxPlayers").GetInt32());
    }

    [Fact]
    public async Task ACapacityLargerThanThePacketFieldHolds_IsClampedRatherThanWrapped()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url, id => id.MaxPlayersOverride = 100_000);

        JsonElement packet = default;
        await RunAsync(cfg, RegistryWith(Backend("hub", 10)),
            async _ => packet = await master.WaitForAsync("register"));

        // maxPlayers is a ushort on the wire. Unclamped, 100000 would come out as 34464.
        Assert.Equal(ushort.MaxValue, packet.GetProperty("maxPlayers").GetInt32());
    }

    // ---- the mod list ----

    [Fact]
    public async Task TheDefaultModList_IsTheUnionAcrossLiveBackends()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url);

        JsonElement packet = default;
        await RunAsync(cfg, RegistryWith(
                Backend("hub", 10, ("nimbusclient", "1.0.0"), ("redirectfix", "1.1.0")),
                Backend("creative", 10, ("nimbusclient", "1.0.0"), ("carryon", "2.0.0"))),
            async _ => packet = await master.WaitForAsync("register"));

        // A client filtering the server list by the mods it has needs the whole network's set,
        // listed once each.
        Assert.Equal(new[] { "carryon", "nimbusclient", "redirectfix" }, ModIds(packet).Order().ToArray());
    }

    [Fact]
    public async Task WhenTwoBackendsDisagreeOnAModVersion_TheHigherOneIsAdvertised()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url);

        JsonElement packet = default;
        await RunAsync(cfg, RegistryWith(
                Backend("hub", 10, ("carryon", "1.9.0")),
                Backend("creative", 10, ("carryon", "2.0.0"))),
            async _ => packet = await master.WaitForAsync("register"));

        var mod = packet.GetProperty("Mods").EnumerateArray().Single();
        // Mid-rollout the two backends disagree. Advertising the older one tells a client it can
        // join with a version half the network will refuse.
        Assert.Equal("2.0.0", mod.GetProperty("version").GetString());
    }

    [Fact]
    public async Task AStaleBackendsMods_AreNotAdvertised()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url);

        var clock = new FakeClock();
        var backends = new BackendRegistry(new RegistryConfig { BackendStaleSeconds = 20 }, clock);
        backends.Upsert(Backend("gone", 10, ("retiredmod", "1.0.0")));
        clock.Advance(TimeSpan.FromSeconds(60));
        backends.Upsert(Backend("hub", 10, ("nimbusclient", "1.0.0")));

        JsonElement packet = default;
        await RunAsync(cfg, backends, async _ => packet = await master.WaitForAsync("register"));

        Assert.Equal(new[] { "nimbusclient" }, ModIds(packet));
    }

    [Fact]
    public async Task AModWithNoIdInABackendHeartbeat_IsDroppedRatherThanAdvertisedBlank()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url);

        JsonElement packet = default;
        await RunAsync(cfg, RegistryWith(Backend("hub", 10, ("", "1.0.0"), ("carryon", "2.0.0"))),
            async _ => packet = await master.WaitForAsync("register"));

        Assert.Equal(new[] { "carryon" }, ModIds(packet));
    }

    [Fact]
    public async Task AnExplicitModList_ReplacesWhatTheBackendsReport()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url, id =>
        {
            id.ModSource = "explicit";
            id.ExplicitMods = new[] { new ExplicitMod { Id = "nimbusclient", Version = "1.0.0" } };
        });

        JsonElement packet = default;
        await RunAsync(cfg, RegistryWith(Backend("hub", 10, ("something-internal", "0.1.0"))),
            async _ => packet = await master.WaitForAsync("register"));

        // An operator whose backends run server-side mods clients do not need says so here, and
        // what they say is the whole list.
        Assert.Equal(new[] { "nimbusclient" }, ModIds(packet));
    }

    [Fact]
    public async Task AModListMirroredFromOneBackend_IgnoresTheOthers()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url, id => id.ModSource = "backend:hub");

        JsonElement packet = default;
        await RunAsync(cfg, RegistryWith(
                Backend("hub", 10, ("nimbusclient", "1.0.0")),
                Backend("modded", 10, ("a-hundred-mods", "1.0.0"))),
            async _ => packet = await master.WaitForAsync("register"));

        // Networks where one backend is the front door and the rest are opt-in advertise the
        // front door's requirements, so the list is not scary to a vanilla client.
        Assert.Equal(new[] { "nimbusclient" }, ModIds(packet));
    }

    [Fact]
    public async Task AModListMirroredFromABackendThatIsNotThere_IsEmptyRatherThanEverything()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url, id => id.ModSource = "backend:typo");

        JsonElement packet = default;
        await RunAsync(cfg, RegistryWith(Backend("hub", 10, ("nimbusclient", "1.0.0"))),
            async _ => packet = await master.WaitForAsync("register"));

        // Falling back to the aggregate would quietly advertise a list the operator did not ask
        // for. Empty is wrong in a way they will notice.
        Assert.Empty(ModIds(packet));
    }

    // ---- waiting for a backend before the first register ----

    [Fact]
    public async Task TheFirstRegister_WaitsForABackendRatherThanAdvertisingAnEmptyNetwork()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url);
        var backends = new BackendRegistry(new RegistryConfig());

        JsonElement packet = default;
        await RunAsync(cfg, backends, async _ =>
        {
            // Nothing has heartbeated yet. Registering now would put a 0-slot entry on the list,
            // and a heartbeat cannot correct maxPlayers afterwards.
            await Task.Delay(500);
            Assert.Empty(master.Calls);

            backends.Upsert(Backend("hub", 24));
            packet = await master.WaitForAsync("register");
        });

        Assert.Equal(24, packet.GetProperty("maxPlayers").GetInt32());
    }

    [Fact]
    public async Task ABackendWithNoSlots_DoesNotCountAsTheNetworkBeingUp()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url);
        var backends = new BackendRegistry(new RegistryConfig());
        // A backend that answers but reports zero capacity is still starting up.
        backends.Upsert(Backend("hub", 0));

        await RunAsync(cfg, backends, async _ =>
        {
            await Task.Delay(500);
            Assert.Empty(master.Calls);
        });
    }

    [Fact]
    public async Task WithAnExplicitMaxPlayers_OneLiveBackendIsEnoughToRegister()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url, id => id.MaxPlayersOverride = 64);
        var backends = new BackendRegistry(new RegistryConfig());
        backends.Upsert(Backend("hub", 0));

        JsonElement packet = default;
        // The wait exists to get a real capacity into the first packet. An operator who stated
        // the capacity outright has already supplied it.
        await RunAsync(cfg, backends, async _ => packet = await master.WaitForAsync("register"));

        Assert.Equal(64, packet.GetProperty("maxPlayers").GetInt32());
    }

    // ---- what the master server answers ----

    [Fact]
    public async Task OnShutdown_TheNetworkIsUnregisteredWithTheTokenItWasGiven()
    {
        await using var master = await FakeMasterServer.StartAsync();
        master.OnRegister = () => FakeMasterServer.Ok("token-abc");
        var cfg = Advertising(master.Url);

        await RunAsync(cfg, RegistryWith(Backend("hub", 10)), async log =>
        {
            await master.WaitForAsync("register");
            // Not just "the register arrived": the token only exists once the broadcaster has
            // read the answer, and stopping before then would find nothing to unregister with.
            await log.WaitForAsync("master server registered ok");
        });

        // Left registered, the entry sits on the list until the master server times it out and
        // every click on it in the meantime is a failed connection.
        var unregister = Assert.Single(master.Bodies("unregister"));
        Assert.Equal("token-abc", unregister.GetProperty("token").GetString());
    }

    [Fact]
    public async Task ABlacklistedNetwork_IsNotTreatedAsRegistered()
    {
        await using var master = await FakeMasterServer.StartAsync();
        master.OnRegister = () => FakeMasterServer.Status("blacklisted", "network is blacklisted");
        var cfg = Advertising(master.Url);

        await RunAsync(cfg, RegistryWith(Backend("hub", 10)), async log =>
        {
            await master.WaitForAsync("register");
            await log.WaitForAsync("blacklisted");
        });

        // No token was handed out, so there is nothing to unregister, and the registry itself
        // carries on: players who know the address can still connect directly.
        Assert.Empty(master.Bodies("unregister"));
    }

    [Fact]
    public async Task ARejectedRegister_LeavesNothingToUnregister()
    {
        await using var master = await FakeMasterServer.StartAsync();
        master.OnRegister = () => FakeMasterServer.Status("error", "bad game version");
        var cfg = Advertising(master.Url);

        await RunAsync(cfg, RegistryWith(Backend("hub", 10)), async log =>
        {
            await master.WaitForAsync("register");
            await log.WaitForAsync("register rejected");
        });

        Assert.Empty(master.Bodies("unregister"));
    }

    [Fact]
    public async Task AMasterServerAnsweringWithAnHttpError_LeavesTheRegistryRunning()
    {
        await using var master = await FakeMasterServer.StartAsync();
        master.OnRegister = () => Microsoft.AspNetCore.Http.Results.StatusCode(503);
        var cfg = Advertising(master.Url);

        await RunAsync(cfg, RegistryWith(Backend("hub", 10)), async log =>
        {
            await master.WaitForAsync("register");
            // The client turns a non-success status into a "timeout" answer, which the
            // broadcaster reports as a rejection rather than treating as registered.
            await log.WaitForAsync("register rejected");
        });

        Assert.Empty(master.Bodies("unregister"));
    }

    [Fact]
    public async Task AMasterServerAnsweringWithSomethingThatIsNotJson_LeavesTheRegistryRunning()
    {
        await using var master = await FakeMasterServer.StartAsync();
        master.OnRegister = () => Microsoft.AspNetCore.Http.Results.Text("<html>maintenance</html>", "text/html");
        var cfg = Advertising(master.Url);

        await RunAsync(cfg, RegistryWith(Backend("hub", 10)), async log =>
        {
            await master.WaitForAsync("register");
            await log.WaitForAsync("master server register failed");
        });

        Assert.Empty(master.Bodies("unregister"));
    }

    [Fact]
    public async Task AMasterServerThatIsDownAltogether_DoesNotTakeTheRegistryWithIt()
    {
        // The master server being unreachable is the common case: it is a third-party service and
        // the registry is what keeps a private network working while it is down.
        var cfg = Advertising(FakeMasterServer.DeadUrl());

        var log = await RunAsync(cfg, RegistryWith(Backend("hub", 10)),
            async l => await l.WaitForAsync("master server register failed"));

        // It tried, it failed, and it neither faulted (RunAsync checks that) nor came away
        // thinking it was registered.
        Assert.Contains(log.Lines, l => l.Contains("master server register failed"));
        Assert.DoesNotContain(log.Lines, l => l.Contains("registered ok"));
    }

    [Fact]
    public async Task AnUnregisterThatFailsAtShutdown_IsSwallowed()
    {
        await using var master = await FakeMasterServer.StartAsync();
        master.OnUnregister = () => throw new InvalidOperationException("master server fell over");
        var cfg = Advertising(master.Url);

        // Nothing can be done about it by then and a throw here would come out of host shutdown.
        await RunAsync(cfg, RegistryWith(Backend("hub", 10)), async log =>
        {
            await master.WaitForAsync("register");
            await log.WaitForAsync("master server registered ok");
        });

        Assert.Single(master.Bodies("unregister"));
    }

    // ---- stopping before the first register ----

    [Fact]
    public async Task AShutdownDuringTheWaitForABackend_StopsWithoutAdvertisingAnything()
    {
        await using var master = await FakeMasterServer.StartAsync();
        var cfg = Advertising(master.Url);

        // A registry stopped moments after it started, before any backend reported in. It must
        // come down rather than sit out the rest of the 30s window.
        await RunAsync(cfg, new BackendRegistry(new RegistryConfig()), async _ => await Task.Delay(100));

        Assert.Empty(master.Calls);
    }
}
