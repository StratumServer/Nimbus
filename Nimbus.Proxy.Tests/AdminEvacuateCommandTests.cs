using System.Diagnostics;
using System.Text.Json;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The eviction half of a drain, driven over the real admin socket. Drain is the cordon and
/// evacuate is the move, so these pin both the sweep itself and the split between the two: an
/// evacuate of a source nobody cordoned first has to say out loud that new joins can land on it
/// while the sweep is still walking.
///
/// Every transfer goes through the same path a hand-typed swap takes, so the interesting cases
/// are the ones where that path refuses one player and not another: what an operator needs off
/// the answer is which of their players moved, which did not, and why.
/// </summary>
public class AdminEvacuateCommandTests
{
    [Fact]
    public async Task Evacuate_MovesEverySessionOffTheSourceOntoTheNamedTarget()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        using var creative = RegisterTarget(harness, "creative");
        var alice = await JoinHubAsync(harness, "uid-1", "alice");
        var bob = await JoinHubAsync(harness, "uid-2", "bob");

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", to = "creative", paceMs = 0 });

        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        Assert.True(reply.GetProperty("completed").GetBoolean());
        AssertCounts(reply, matched: 2, moved: 2, failed: 0, skipped: 0);

        var outcomes = Sessions(reply).ToDictionary(s => s.GetProperty("id").GetInt64());
        foreach (var id in new[] { alice.Id, bob.Id })
        {
            Assert.Equal("moved", outcomes[id].GetProperty("outcome").GetString());
            Assert.Equal("creative", outcomes[id].GetProperty("target").GetString());
            Assert.Equal(JsonValueKind.Null, outcomes[id].GetProperty("reason").ValueKind);
        }
        // Named rather than inferred from the entries: an operator reads the top of the answer.
        Assert.Equal("hub", reply.GetProperty("source").GetString());
        Assert.Equal("creative", reply.GetProperty("target").GetString());

        // Both players are expected back through the proxy, and both reconnects are claimed for
        // creative in advance, which is what makes the redirect land where it was aimed.
        var staged = harness.Proxy.Stickies.Snapshot();
        Assert.Equal(new[] { "uid-1", "uid-2" }, staged.Select(s => s.Uid).OrderBy(u => u));
        Assert.All(staged, s => Assert.Equal("creative", s.Target.ServerId));
    }

    [Fact]
    public async Task EvacuateWithAPlayerBannedFromTheTarget_MovesTheRestAndNamesTheOneItCouldNot()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        using var creative = RegisterTarget(harness, "creative");
        var alice = await JoinHubAsync(harness, "uid-1", "alice");
        var griefer = await JoinHubAsync(harness, "uid-2", "griefer");

        harness.Registry.Bans = new List<NetworkBan>
        {
            new() { PlayerUid = "uid-2", PlayerName = "griefer", ServerId = "creative", Reason = "griefing" },
        };
        await harness.Proxy.Bans.RefreshAsync();
        Assert.NotNull(harness.Proxy.Bans.FindBlocking("uid-2", "creative"));

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", to = "creative", paceMs = 0 });

        // One refusal is not an aborted evacuation: the players who can go, go.
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        AssertCounts(reply, matched: 2, moved: 1, failed: 1, skipped: 0);

        var outcomes = Sessions(reply).ToDictionary(s => s.GetProperty("id").GetInt64());
        Assert.Equal("moved", outcomes[alice.Id].GetProperty("outcome").GetString());
        Assert.Equal("failed", outcomes[griefer.Id].GetProperty("outcome").GetString());
        // The reason is the ban's own words, not "transfer failed": it is what tells the operator
        // to lift the ban or pick another target rather than to retry the same command.
        Assert.Equal("player is banned from creative", outcomes[griefer.Id].GetProperty("reason").GetString());

        // The player who stayed is still connected to hub, and nothing was staged for them.
        Assert.Equal("uid-1", Assert.Single(harness.Proxy.Stickies.Snapshot()).Uid);
        Assert.False(await griefer.DroppedAsync(500), "a refused transfer must leave the player where they are");
    }

    [Fact]
    public async Task EvacuateWithNoTarget_LetsTheRouterPickAnywhereThatIsNotTheSource()
    {
        await using var harness = await AdminHarness.StartAsync(
            cfg => cfg.Try = new List<string> { "hub", "creative" }, serverIds: new[] { "hub", "creative" });
        await JoinHubAsync(harness, "uid-1", "alice");

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", paceMs = 0 });

        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        AssertCounts(reply, matched: 1, moved: 1, failed: 0, skipped: 0);
        // No fixed target at the top of the answer, because there was not one.
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("target").ValueKind);
        Assert.Equal("creative", Single(reply).GetProperty("target").GetString());
    }

    [Fact]
    public async Task EvacuateWhenTheOnlyBackendTheRouterOffersIsTheSource_FailsThatSessionByName()
    {
        // try is [hub] alone, so excluding hub leaves the router with nothing to offer.
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        var alice = await JoinHubAsync(harness, "uid-1", "alice");

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", paceMs = 0 });

        // The sweep ran; it is the one session in it that had nowhere to go.
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        AssertCounts(reply, matched: 1, moved: 0, failed: 1, skipped: 0);
        Assert.Equal("the only healthy backend the router offers is hub itself",
            Single(reply).GetProperty("reason").GetString());
        Assert.False(await alice.DroppedAsync(500), "nobody is disconnected for having nowhere to go");
    }

    [Fact]
    public async Task EvacuateToTheSourceItself_IsRefusedBeforeAnybodyIsTouched()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        await JoinHubAsync(harness, "uid-1", "alice");

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", to = "hub" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("target 'hub' is the source; evacuate needs somewhere else to put them",
            reply.GetProperty("reason").GetString());
        Assert.Contains("evacuate <serverId>", reply.GetProperty("usage").GetString());
        Assert.Empty(harness.Proxy.Stickies.Snapshot());
    }

    [Fact]
    public async Task EvacuateOfABackendNobodyHasHeardOf_IsRefusedRatherThanReportedAsAnEmptySweep()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "typo" });

        // "0 players moved" would read as a successful evacuation of a backend that does not
        // exist, which is the answer an operator acts on when they have mistyped the name.
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("unknown serverId 'typo'", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task EvacuateToABackendTheRegistryWillNotHaveIsRefusedTheWaySwapRefusesIt()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        await JoinHubAsync(harness, "uid-1", "alice");

        Assert.Equal("unknown serverId 'typo' in registry",
            (await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", to = "typo" }))
                .GetProperty("reason").GetString());

        harness.Registry.Backends["creative"] = new BackendSnapshot
        {
            ServerId = "creative", PublicHost = "127.0.0.1", PublicPort = 42430, Stale = true,
        };
        Assert.Equal("target 'creative' is stale (no recent heartbeat)",
            (await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", to = "creative" }))
                .GetProperty("reason").GetString());

        harness.Registry.Backends["creative"] = new BackendSnapshot
        {
            ServerId = "creative", PublicHost = "127.0.0.1", PublicPort = 42430, Maintenance = true,
        };
        Assert.Equal("target 'creative' is in maintenance",
            (await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", to = "creative" }))
                .GetProperty("reason").GetString());

        // A target checked once at the top rather than once per player: nobody was moved into any
        // of the three, and no reservation was spent finding that out.
        Assert.Empty(harness.Proxy.Stickies.Snapshot());
    }

    [Fact]
    public async Task EvacuateToAnUnreachableBackend_IsRefusedBeforeTheSweepStarts()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        await JoinHubAsync(harness, "uid-1", "alice");
        var dead = SessionHarness.DeadEndpoint();
        harness.Registry.Backends["creative"] = new BackendSnapshot
        {
            ServerId = "creative", PublicHost = "127.0.0.1", PublicPort = dead.Port,
        };

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", to = "creative" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("unreachable (tcp probe)", reply.GetProperty("reason").GetString());
        Assert.DoesNotContain(harness.Registry.MintsSoFar(), m => m.TargetServerId == "creative");
    }

    [Fact]
    public async Task EvacuateWhereTheRouterPicksABackendThatWillNotAnswer_FailsThatSessionRatherThanKickingIt()
    {
        // No registry, so the pool is the whole story and the second name in it points at a port
        // nothing is listening on.
        var dead = SessionHarness.DeadEndpoint();
        await using var harness = await AdminHarness.StartAsync(cfg =>
        {
            cfg.Servers["gone"] = $"127.0.0.1:{dead.Port}";
            cfg.Try = new List<string> { "hub", "gone" };
        }, withRegistry: false, serverIds: new[] { "hub" });
        var alice = await JoinHubAsync(harness, "uid-1", "alice");

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", paceMs = 0 });

        // The probe is what keeps a redirect from closing a working session onto a dead socket.
        AssertCounts(reply, matched: 1, moved: 0, failed: 1, skipped: 0);
        Assert.Contains("unreachable (tcp probe)", Single(reply).GetProperty("reason").GetString());
        Assert.False(await alice.DroppedAsync(500), "the player stays put when the target will not answer");
    }

    [Fact]
    public async Task EvacuateWithTheRegistryOff_ResolvesTheTargetOutOfTheConfiguredPool()
    {
        await using var harness = await AdminHarness.StartAsync(withRegistry: false, serverIds: new[] { "hub", "creative" });
        await JoinHubAsync(harness, "uid-1", "alice");

        // A registry-less network still has to be able to empty a backend, and [servers] is the
        // only place a name can be resolved from when there is nothing to ask.
        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", to = "creative", paceMs = 0 });

        AssertCounts(reply, matched: 1, moved: 1, failed: 0, skipped: 0);
        Assert.Equal("creative", Single(reply).GetProperty("target").GetString());

        var refused = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", to = "typo" });
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown serverId 'typo': not in the backend pool and the registry is disabled",
            refused.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task EvacuateWhenEveryOtherBackendIsDrained_SaysTheRouterHasNothingLeft()
    {
        await using var harness = await AdminHarness.StartAsync(
            cfg => cfg.Try = new List<string> { "hub", "creative" }, serverIds: new[] { "hub", "creative" });
        await JoinHubAsync(harness, "uid-1", "alice");
        await harness.RunAsync(new { cmd = "drain", serverId = "hub" });
        await harness.RunAsync(new { cmd = "drain", serverId = "creative" });

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", paceMs = 0 });

        // Cordoning the whole network is a state an operator can reach mid-rolling-restart, and
        // the answer names it rather than reporting a transfer that quietly went nowhere.
        AssertCounts(reply, matched: 1, moved: 0, failed: 1, skipped: 0);
        Assert.Contains("the router has no healthy backend", Single(reply).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task EvacuateWithATransferModeTheProxyDoesNotHave_IsRefusedBeforeTheSweep()
    {
        await using var harness = await AdminHarness.StartAsync(
            cfg => cfg.Transfers.DefaultMode = "teleport", serverIds: new[] { "hub", "creative" });
        await JoinHubAsync(harness, "uid-1", "alice");

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", to = "creative" });

        // Every session would fail for the same reason, so it is one refusal at the top rather
        // than the same sentence repeated once per player.
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("unknown transfer mode 'teleport'", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task EvacuateOfASourceNobodyDrained_WarnsThatNewJoinsCanRepopulateItMidSweep()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        using var creative = RegisterTarget(harness, "creative");
        await JoinHubAsync(harness, "uid-1", "alice");

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", to = "creative", paceMs = 0 });

        // Evacuate empties a backend, it does not close it. Without the cordon the sweep can
        // finish onto a source that has already taken someone new, and the answer says so.
        Assert.False(reply.GetProperty("drained").GetBoolean());
        string warning = reply.GetProperty("warning").GetString()!;
        Assert.Contains("hub is not drained", warning);
        Assert.Contains("nimctl drain hub", warning);
    }

    [Fact]
    public async Task EvacuateOfASourceThatWasDrainedFirst_HasNothingToWarnAbout()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        using var creative = RegisterTarget(harness, "creative");
        await JoinHubAsync(harness, "uid-1", "alice");
        Assert.True((await harness.RunAsync(new { cmd = "drain", serverId = "hub" })).GetProperty("ok").GetBoolean());

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", to = "creative", paceMs = 0 });

        Assert.True(reply.GetProperty("drained").GetBoolean());
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("warning").ValueKind);
        // And draining the source does not stop it being evacuated off, which is the whole point
        // of typing the two commands in that order.
        AssertCounts(reply, matched: 1, moved: 1, failed: 0, skipped: 0);
    }

    [Fact]
    public async Task EvacuateOfASessionThatHasNotIdentifiedItself_SkipsItRatherThanCountingAFailure()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        using var creative = RegisterTarget(harness, "creative");
        await JoinHubAsync(harness, "uid-1", "alice");
        var stock = await harness.ConnectWithoutIdentifyingAsync();

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", to = "creative", paceMs = 0 });

        // A session with no identity has nothing to reserve a slot for and nothing to send. It is
        // not a failure to fix, so it is counted apart from the ones that are.
        AssertCounts(reply, matched: 2, moved: 1, failed: 0, skipped: 1);
        var skipped = Sessions(reply).Single(s => s.GetProperty("id").GetInt64() == stock.Id);
        Assert.Equal("skipped", skipped.GetProperty("outcome").GetString());
        Assert.Contains("no Identification captured yet", skipped.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task EvacuateOfABackendWithNobodyOnIt_IsAnEmptySweepRatherThanAFailure()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        using var hub = RegisterTarget(harness, "hub");

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "creative", to = "hub", paceMs = 0 });

        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        AssertCounts(reply, matched: 0, moved: 0, failed: 0, skipped: 0);
        Assert.Empty(Sessions(reply));
    }

    [Fact]
    public async Task EvacuatePacesTheTransfers_AndSaysWhatPaceItRanAt()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        using var creative = RegisterTarget(harness, "creative");
        await JoinHubAsync(harness, "uid-1", "alice");
        await JoinHubAsync(harness, "uid-2", "bob");

        const int pace = 700;
        var clock = Stopwatch.StartNew();
        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", to = "creative", paceMs = pace });
        clock.Stop();

        AssertCounts(reply, matched: 2, moved: 2, failed: 0, skipped: 0);
        Assert.Equal(pace, reply.GetProperty("paceMs").GetInt32());
        // A lower bound only: two transfers one pace apart cannot have taken less than one pace,
        // and how much longer they took is the machine's business rather than the command's.
        Assert.True(clock.ElapsedMilliseconds >= pace,
            $"two transfers {pace}ms apart came back in {clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task EvacuateAtNoPaceAtAll_IsAllowedForWhenTheHerdIsThePoint()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        using var creative = RegisterTarget(harness, "creative");
        await JoinHubAsync(harness, "uid-1", "alice");
        await JoinHubAsync(harness, "uid-2", "bob");

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", to = "creative", paceMs = 0 });

        // Zero is a real answer rather than "unset", so an operator emptying a backend that is
        // about to be killed does not have to wait out a default they did not ask for.
        Assert.Equal(0, reply.GetProperty("paceMs").GetInt32());
        AssertCounts(reply, matched: 2, moved: 2, failed: 0, skipped: 0);
    }

    [Fact]
    public async Task EvacuateWithNoPaceNamed_UsesTheDocumentedDefault()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        using var creative = RegisterTarget(harness, "creative");

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", to = "creative" });

        Assert.Equal(250, reply.GetProperty("paceMs").GetInt32());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5001)]
    public async Task EvacuateAtAPaceTheSweepCannotHold_IsRefusedWithTheRange(int paceMs)
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", paceMs });

        // The command answers when the sweep is done, so an unbounded pace is a reply nobody is
        // still on the socket to read.
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("between 0 and 5000", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task EvacuateWithAPaceThatIsNotANumber_IsRefusedRatherThanQuietlyDefaulted()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub", paceMs = "fast" });

        // Falling back to 250 here would run an evacuation at a pace nobody asked for and report
        // it as the one that was requested.
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("paceMs must be", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task EvacuateWithNoBackendNamed_SaysWhatIsMissing()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "evacuate" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("serverId", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task EvacuateUnderItsAlias_ReachesTheSameHandler()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        using var creative = RegisterTarget(harness, "creative");
        await JoinHubAsync(harness, "uid-1", "alice");

        var reply = await harness.RunAsync(new { cmd = "evac", serverId = "hub", to = "creative", paceMs = 0 });

        AssertCounts(reply, matched: 1, moved: 1, failed: 0, skipped: 0);
    }

    [Fact]
    public async Task Help_ListsEvacuateWithThePermissionToGrantAndTheDrainSplitInItsSummary()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "help" });

        var evacuate = reply.GetProperty("commands").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "evacuate");
        Assert.Equal("nimbus.command.evacuate", evacuate.GetProperty("permission").GetString());
        // Moving every player off a backend is not something to hand out with `drain`, so it has
        // its own permission, and the usage carries the order the two are meant to be typed in.
        Assert.Contains("drain the source first", evacuate.GetProperty("usage").GetString());
        Assert.Contains("evac", evacuate.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task EvacuateWithoutItsOwnPermission_IsDeniedEvenWhenDrainIsGranted()
    {
        await using var harness = await AdminHarness.StartAsync(
            cfg => cfg.Admin.GrantedPermissions = new List<string> { "nimbus.command.drain" },
            serverIds: new[] { "hub", "creative" });

        var reply = await harness.RunAsync(new { cmd = "evacuate", serverId = "hub" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("nimbus.command.evacuate", reply.GetProperty("permission").GetString());
    }

    // ---- harness ----

    /// <summary>A player on <c>hub</c>, waited on until the session is actually pumped to it:
    /// evacuate matches sessions on the backend they are attached to, and the uid turns up first.
    /// </summary>
    private static async Task<PlayerHandle> JoinHubAsync(AdminHarness harness, string uid, string name)
    {
        var handle = await harness.JoinAsync(uid, name);
        await AdminHarness.WaitFor(() => handle.Session.CurrentBackend?.ServerId == "hub",
            $"session {handle.Id} never attached to hub");
        return handle;
    }

    /// <summary>Points the registry at a live listener under <paramref name="serverId"/>, so the
    /// target resolves and answers the probe the way a running backend would.</summary>
    private static RecordingBackend RegisterTarget(AdminHarness harness, string serverId)
    {
        var backend = new RecordingBackend();
        harness.Registry.Backends[serverId] = new BackendSnapshot
        {
            ServerId = serverId,
            PublicHost = "127.0.0.1",
            PublicPort = backend.Port,
        };
        return backend;
    }

    private static IEnumerable<JsonElement> Sessions(JsonElement reply)
        => reply.GetProperty("sessions").EnumerateArray();

    private static JsonElement Single(JsonElement reply) => Sessions(reply).Single();

    private static void AssertCounts(JsonElement reply, int matched, int moved, int failed, int skipped)
    {
        var counts = reply.GetProperty("counts");
        Assert.Equal(matched, counts.GetProperty("matched").GetInt32());
        Assert.Equal(moved, counts.GetProperty("moved").GetInt32());
        Assert.Equal(failed, counts.GetProperty("failed").GetInt32());
        Assert.Equal(skipped, counts.GetProperty("skipped").GetInt32());
    }
}
