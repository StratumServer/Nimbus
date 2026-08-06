using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The read-and-steer half of the admin surface: what an operator looks at before deciding
/// (help, route, servers, sticky, status), the drain switch they flip during a rolling restart,
/// and the swap that moves a player. Driven over the real admin socket, so every assertion is on
/// the JSON line nimctl prints.
/// </summary>
public class AdminOperationsCommandTests
{
    // ---- help ----

    [Fact]
    public async Task Help_ListsEveryCommandWithWhatToTypeAndWhatItNeeds()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "help" });

        var commands = reply.GetProperty("commands").EnumerateArray().ToList();
        Assert.NotEmpty(commands);
        // Sorted, so the output is stable between runs and diffable in a runbook.
        var names = commands.Select(c => c.GetProperty("name").GetString()!).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), names);

        var swap = commands.Single(c => c.GetProperty("name").GetString() == "swap");
        // The three things an operator needs off this: what it does, how to type it, and which
        // permission to grant when it comes back denied.
        Assert.False(string.IsNullOrWhiteSpace(swap.GetProperty("summary").GetString()));
        Assert.Contains("swap", swap.GetProperty("usage").GetString());
        Assert.Equal("nimbus.command.swap", swap.GetProperty("permission").GetString());
        Assert.Contains("send", swap.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task Help_OnlyListsWhatTheCallerIsAllowedToRun()
    {
        await using var harness = await AdminHarness.StartAsync(
            cfg => cfg.Admin.GrantedPermissions = new List<string> { "nimbus.command.help", "nimbus.command.ping" });

        var reply = await harness.RunAsync(new { cmd = "help" });

        // Listing commands the caller cannot run turns help into a menu of refusals.
        var names = reply.GetProperty("commands").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "help", "ping" }, names);
    }

    [Fact]
    public async Task HelpUnderItsAlias_IsTheSameAnswer()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "?" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.NotEmpty(reply.GetProperty("commands").EnumerateArray());
    }

    // ---- route ----

    [Fact]
    public async Task Route_ShowsTheConfiguredPoolWithWhatTheRegistryKnowsAboutIt()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        harness.Registry.Snapshot = new NetworkSnapshot
        {
            Backends =
            {
                new BackendSnapshot { ServerId = "hub", Players = 4, MaxPlayers = 20, Stale = false },
                new BackendSnapshot { ServerId = "creative", Players = 0, MaxPlayers = 10, Stale = true },
            },
        };

        var reply = await harness.RunAsync(new { cmd = "route" });

        var candidates = reply.GetProperty("candidates").EnumerateArray()
            .ToDictionary(c => c.GetProperty("serverId").GetString()!);
        Assert.Equal(2, candidates.Count);

        // The live half: which backends the registry has actually heard from, and how full.
        Assert.True(candidates["hub"].GetProperty("known").GetBoolean());
        Assert.False(candidates["hub"].GetProperty("stale").GetBoolean());
        Assert.Equal(4, candidates["hub"].GetProperty("players").GetInt32());
        Assert.Equal(20, candidates["hub"].GetProperty("maxPlayers").GetInt32());
        Assert.True(candidates["creative"].GetProperty("stale").GetBoolean());
    }

    [Fact]
    public async Task Route_MarksTheBackendsAnOperatorHasDrained()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });

        Assert.True((await harness.RunAsync(new { cmd = "drain", serverId = "creative" })).GetProperty("ok").GetBoolean());
        var reply = await harness.RunAsync(new { cmd = "route" });

        Assert.Contains("creative", reply.GetProperty("drained").EnumerateArray().Select(d => d.GetString()));
        var candidates = reply.GetProperty("candidates").EnumerateArray()
            .ToDictionary(c => c.GetProperty("serverId").GetString()!);
        Assert.True(candidates["creative"].GetProperty("drained").GetBoolean());
        Assert.False(candidates["hub"].GetProperty("drained").GetBoolean());
    }

    [Fact]
    public async Task RouteWithoutARegistry_StillShowsTheConfiguredPool()
    {
        await using var harness = await AdminHarness.StartAsync(withRegistry: false, serverIds: new[] { "hub" });

        var reply = await harness.RunAsync(new { cmd = "route" });

        // The pool comes from the config file, so it is knowable with the registry off. What is
        // unknowable is the health, and that says so rather than guessing.
        var candidate = reply.GetProperty("candidates").EnumerateArray().Single();
        Assert.Equal("hub", candidate.GetProperty("serverId").GetString());
        Assert.False(candidate.GetProperty("known").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, candidate.GetProperty("stale").ValueKind);
    }

    // ---- drain ----

    [Fact]
    public async Task Drain_StopsNewSessionsGoingToABackendAndUndrainLetsThemBack()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });

        var drained = await harness.RunAsync(new { cmd = "drain", serverId = "creative" });
        Assert.True(drained.GetProperty("ok").GetBoolean());
        // `added` distinguishes "I just drained it" from "it was already drained", which is what
        // an operator running the same command twice during a rolling restart wants to know.
        Assert.True(drained.GetProperty("added").GetBoolean());
        Assert.True(harness.Proxy.Router.IsDrained("creative"));

        Assert.False((await harness.RunAsync(new { cmd = "drain", serverId = "creative" }))
            .GetProperty("added").GetBoolean());

        var undrained = await harness.RunAsync(new { cmd = "undrain", serverId = "creative" });
        Assert.True(undrained.GetProperty("removed").GetBoolean());
        Assert.False(harness.Proxy.Router.IsDrained("creative"));

        Assert.False((await harness.RunAsync(new { cmd = "undrain", serverId = "creative" }))
            .GetProperty("removed").GetBoolean());
    }

    [Fact]
    public async Task DrainWithNoBackendNamed_SaysWhatIsMissing()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "drain" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("serverId", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task UndrainUnderItsAlias_ReachesTheSameHandler()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        await harness.RunAsync(new { cmd = "drain", serverId = "creative" });

        Assert.True((await harness.RunAsync(new { cmd = "resume", serverId = "creative" }))
            .GetProperty("removed").GetBoolean());
    }

    // ---- servers ----

    [Fact]
    public async Task Servers_HandsBackTheRegistrySnapshot()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.Snapshot = new NetworkSnapshot
        {
            TotalPlayers = 7,
            TotalCapacity = 40,
            Backends = { new BackendSnapshot { ServerId = "hub", Players = 7, MaxPlayers = 40 } },
        };

        var reply = await harness.RunAsync(new { cmd = "servers" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        // The snapshot is serialised as the model rather than reshaped, so its keys are the
        // model's: PascalCase, unlike the camelCase every command's own fields use.
        var snapshot = reply.GetProperty("snapshot");
        Assert.Equal(7, snapshot.GetProperty("TotalPlayers").GetInt32());
        Assert.Equal(40, snapshot.GetProperty("TotalCapacity").GetInt32());
        Assert.Equal("hub", snapshot.GetProperty("Backends").EnumerateArray().Single()
            .GetProperty("ServerId").GetString());
    }

    [Fact]
    public async Task ServersWithoutARegistry_SaysSoRatherThanReturningNothing()
    {
        await using var harness = await AdminHarness.StartAsync(withRegistry: false);

        var reply = await harness.RunAsync(new { cmd = "servers" });

        // An empty snapshot would read as "the network is empty", which is a different
        // emergency to "there is no registry".
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("registry disabled", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ServersWhenTheRegistryCannotAnswer_SaysSoRatherThanReturningNothing()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.Snapshot = null;

        var reply = await harness.RunAsync(new { cmd = "servers" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("registry unavailable", reply.GetProperty("reason").GetString());
    }

    // ---- sticky ----

    [Fact]
    public async Task Sticky_ShowsTheStagedReconnectsWithTheirRemainingLife()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        var target = new BackendEndpoint { Host = "10.0.0.9", Port = 42430, ServerId = "creative" };
        harness.Proxy.Stickies.Stage("uid-1", "203.0.113.7", target, StickyRouteTable.UidTtl, "admin swap", attempts: 2);

        var reply = await harness.RunAsync(new { cmd = "sticky" });

        var entry = reply.GetProperty("entries").EnumerateArray().Single();
        Assert.Equal("uid-1", entry.GetProperty("uid").GetString());
        Assert.Equal("203.0.113.7", entry.GetProperty("clientIp").GetString());
        Assert.Equal("creative", entry.GetProperty("serverId").GetString());
        Assert.Equal(42430, entry.GetProperty("port").GetInt32());
        Assert.Equal("admin swap", entry.GetProperty("reason").GetString());
        // Attempts is what tells an operator a player is being bounced rather than moved.
        Assert.Equal(2, entry.GetProperty("attempts").GetInt32());
        Assert.InRange(entry.GetProperty("ttlSeconds").GetInt32(), 1, (int)StickyRouteTable.UidTtl.TotalSeconds);
    }

    [Fact]
    public async Task StickyWithNothingStaged_IsAnEmptyListRatherThanAFailure()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "stickies" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Empty(reply.GetProperty("entries").EnumerateArray());
    }

    // ---- status ----

    [Fact]
    public async Task Status_ShowsWhereOneSessionHasGotTo()
    {
        await using var harness = await AdminHarness.StartAsync();
        var player = await harness.JoinAsync("uid-1", "alice");

        var reply = await harness.RunAsync(new { cmd = "status", id = player.Id });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("alice", reply.GetProperty("player").GetString());
        Assert.Equal("uid-1", reply.GetProperty("uid").GetString());
        // The phase and whether the Identification was captured are what an operator reads when
        // a player says they are stuck on the loading screen.
        Assert.False(string.IsNullOrWhiteSpace(reply.GetProperty("phase").GetString()));
        Assert.True(reply.GetProperty("identCaptured").GetBoolean());
        Assert.Equal("127.0.0.1", reply.GetProperty("client").GetString());
    }

    [Fact]
    public async Task StatusForASessionThatIsNotThere_SaysSo()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "inspect", id = 999 });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("session not found", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task StatusWithNoSessionId_SaysWhatIsWrongWithTheCall()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "status" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("id", reply.GetProperty("reason").GetString());
    }

    // ---- swap ----

    [Fact]
    public async Task Swap_MovesTheNamedSessionToTheNamedBackend()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        var player = await harness.JoinAsync("uid-1", "alice");
        using var creative = new RecordingBackend();
        harness.Registry.Backends["creative"] = new BackendSnapshot
        {
            ServerId = "creative",
            PublicHost = "127.0.0.1",
            PublicPort = creative.Port,
        };

        var reply = await harness.RunAsync(new { cmd = "swap", id = player.Id, serverId = "creative", mode = "redirect" });

        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        Assert.Equal("redirect", reply.GetProperty("mode").GetString());
        Assert.Equal("creative", reply.GetProperty("target").GetProperty("serverId").GetString());
        // The reconnect the redirect asks for is claimed in advance.
        Assert.Equal("uid-1", Assert.Single(harness.Proxy.Stickies.Snapshot()).Uid);
    }

    [Fact]
    public async Task SwapToABackendTheRegistryHasNotHeardOf_IsRefusedByName()
    {
        await using var harness = await AdminHarness.StartAsync();
        var player = await harness.JoinAsync("uid-1", "alice");

        var reply = await harness.RunAsync(new { cmd = "swap", id = player.Id, serverId = "typo" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown serverId 'typo' in registry", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task SwapToAStaleBackend_IsRefusedBeforeThePlayerIsMoved()
    {
        await using var harness = await AdminHarness.StartAsync();
        var player = await harness.JoinAsync("uid-1", "alice");
        harness.Registry.Backends["creative"] = new BackendSnapshot
        {
            ServerId = "creative", PublicHost = "127.0.0.1", PublicPort = 42430, Stale = true,
        };

        var reply = await harness.RunAsync(new { cmd = "swap", id = player.Id, serverId = "creative" });

        // A backend that stopped heartbeating is one the player would be redirected into a dead
        // socket at, and the redirect closes their working session on the way.
        Assert.Equal("target 'creative' is stale (no recent heartbeat)", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task SwapToABackendInMaintenance_IsRefused()
    {
        await using var harness = await AdminHarness.StartAsync();
        var player = await harness.JoinAsync("uid-1", "alice");
        harness.Registry.Backends["creative"] = new BackendSnapshot
        {
            ServerId = "creative", PublicHost = "127.0.0.1", PublicPort = 42430, Maintenance = true,
        };

        var reply = await harness.RunAsync(new { cmd = "swap", id = player.Id, serverId = "creative" });

        Assert.Equal("target 'creative' is in maintenance", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task SwapToAnUnreachableBackend_IsRefusedBeforeAnyReservationIsMinted()
    {
        await using var harness = await AdminHarness.StartAsync();
        var player = await harness.JoinAsync("uid-1", "alice");
        var dead = SessionHarness.DeadEndpoint();
        harness.Registry.Backends["creative"] = new BackendSnapshot
        {
            ServerId = "creative", PublicHost = "127.0.0.1", PublicPort = dead.Port,
        };

        var reply = await harness.RunAsync(new { cmd = "swap", id = player.Id, serverId = "creative" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("unreachable (tcp probe)", reply.GetProperty("reason").GetString());
        // The probe runs first precisely so nothing is spent on a target that will not answer.
        // The join's own reservation is the only mint; none was made for creative.
        Assert.DoesNotContain(harness.Registry.MintsSoFar(), m => m.TargetServerId == "creative");
        Assert.Empty(harness.Proxy.Stickies.Snapshot());
    }

    [Fact]
    public async Task SwapOfASessionThatIsNotThere_SaysSo()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "swap", id = 999, serverId = "creative" });

        Assert.Equal("session not found", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task SwapWithNoSessionId_SaysWhatIsWrongWithTheCall()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "swap", serverId = "creative" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("id", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task SwapNamingNeitherAServerNorAHostAndPort_SaysWhatToTypeInstead()
    {
        await using var harness = await AdminHarness.StartAsync(withRegistry: false);
        var player = await harness.JoinAsync("uid-1", "alice");

        var reply = await harness.RunAsync(new { cmd = "swap", id = player.Id });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("need either serverId with the registry enabled, or host and port",
            reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task SwapToAHostAndPortWithoutARegistry_Works()
    {
        await using var harness = await AdminHarness.StartAsync(withRegistry: false);
        var player = await harness.JoinAsync("uid-1", "alice");
        using var target = new RecordingBackend();

        var reply = await harness.RunAsync(new
        {
            cmd = "swap", id = player.Id, host = "127.0.0.1", port = target.Port, mode = "redirect",
        });

        // Registry-less networks still have to be able to move a player, which is what the
        // host/port spelling is for.
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        Assert.Equal(target.Port, reply.GetProperty("target").GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task SwapInAModeThatDoesNotExist_IsRefusedByName()
    {
        await using var harness = await AdminHarness.StartAsync(withRegistry: false);
        var player = await harness.JoinAsync("uid-1", "alice");
        using var target = new RecordingBackend();

        var reply = await harness.RunAsync(new
        {
            cmd = "swap", id = player.Id, host = "127.0.0.1", port = target.Port, mode = "teleport",
        });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("unknown mode 'teleport'", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task SwapReportsBothTheModeAskedForAndTheModeUsed()
    {
        await using var harness = await AdminHarness.StartAsync(cfg =>
        {
            cfg.Transfers.AllowSeamless = true;
            cfg.Transfers.RequireSeamlessCapability = true;
            cfg.Transfers.FallbackToRedirectWhenSeamlessUnavailable = true;
        }, withRegistry: false);
        var player = await harness.JoinAsync("uid-1", "alice");
        using var target = new RecordingBackend();

        var reply = await harness.RunAsync(new
        {
            cmd = "swap", id = player.Id, host = "127.0.0.1", port = target.Port, mode = "splice",
        });

        // "splice" is the legacy spelling and normalises to seamless; the player is on a stock
        // client so what actually ran is a redirect. An operator watching a transfer needs both
        // numbers or they cannot tell a fallback from a failure.
        Assert.Equal("seamless", reply.GetProperty("requestedMode").GetString());
        Assert.NotEqual("splice", reply.GetProperty("mode").GetString());
    }
}
