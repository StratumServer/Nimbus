using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// What a session does either side of carrying bytes: which backend it lands on when handlers
/// have their say, what it tells the client when nothing will take it, where it points UDP while
/// it lives, and what it hands to plugins on the way past.
///
/// All of it over real sockets. The UDP pin in particular is not observable any other way: a
/// player whose datagrams keep going to the backend they left carries on walking around on
/// everyone else's screen while standing still on their own.
/// </summary>
public class ProxySessionLifecycleTests
{
    // ---- UDP follows TCP ----

    [Fact]
    public async Task WhileASessionIsUp_ItsClientAddressPointsAtTheBackendItLandedOn()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        await harness.IdentifyAsync();

        await SessionHarness.WaitForAsync(() => harness.UdpOverrides.TryGet(harness.ClientAddress, out _),
            "no UDP override was installed for the connected session");

        Assert.True(harness.UdpOverrides.TryGet(harness.ClientAddress, out var target));
        Assert.Equal(harness.Backends["hub"].Port, target.Port);
        Assert.Equal("hub", target.ServerId);
    }

    [Fact]
    public async Task AfterASeamlessSwap_UdpFollowsToTheNewBackend()
    {
        using var harness = await SessionHarness.StartAsync(cfg =>
        {
            cfg.Transfers.AllowSeamless = true;
            cfg.Transfers.EnableUnsafeSeamlessSplice = true;
        }, "hub");
        using var elsewhere = SessionHarness.ExtraBackend();
        await harness.IdentifyAsync();
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("uid-1"), "the join never reached hub");

        // The unsafe splice is what moves a live session between two different backends, and the
        // datagrams have to move with it or the player desynchronises.
        Assert.Null(await harness.Session.RequestSeamlessAsync(elsewhere.Endpoint("elsewhere"),
            failOnRegistryError: false));

        await SessionHarness.WaitForAsync(
            () => harness.UdpOverrides.TryGet(harness.ClientAddress, out var t) && t.Port == elsewhere.Port,
            "UDP stayed pointed at the backend the player left");
    }

    [Fact]
    public async Task WhenASessionEnds_ItsUdpOverrideIsTakenDown()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        await harness.IdentifyAsync();
        await SessionHarness.WaitForAsync(() => harness.UdpOverrides.TryGet(harness.ClientAddress, out _),
            "no UDP override was installed");

        harness.Session.Close();
        await SessionHarness.WaitForAsync(() => harness.Running.IsCompleted, "the session never finished");

        // The table is keyed on the client address alone, so a stale entry would retarget the
        // next player behind the same NAT or the next holder of a recycled DHCP lease at whatever
        // backend the previous one was last on. It also never went away, which is a slow leak in
        // a proxy that stays up for weeks.
        Assert.False(harness.UdpOverrides.TryGet(harness.ClientAddress, out _));
    }

    // ---- chat surfacing ----

    [Fact]
    public async Task WhatAPlayerTypes_ReachesAPluginThatAskedForIt()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        var seen = new List<PlayerChatEvent>();
        harness.Events.Subscribe<PlayerChatEvent>(evt => { lock (seen) seen.Add(evt); });
        await harness.IdentifyAsync("uid-1", "alice");
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("uid-1"), "the join never reached hub");

        await harness.SendAsync(ChatFrames.Chatline("hello everyone", groupId: 7));

        await SessionHarness.WaitForAsync(() => { lock (seen) return seen.Count > 0; },
            "the chat line never reached the handler");
        PlayerChatEvent evt;
        lock (seen) evt = seen.Single();
        Assert.Equal("hello everyone", evt.Message);
        Assert.Equal(7, evt.GroupId);
        Assert.Equal("alice", evt.Player.Name);
        // Which backend they said it on, so a bridge can label the line.
        Assert.Equal("hub", evt.Server?.ServerId);
    }

    [Fact]
    public async Task AChatLineSurfacedToAPlugin_StillReachesTheBackend()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        harness.Events.Subscribe<PlayerChatEvent>(_ => { });
        await harness.IdentifyAsync();

        await harness.SendAsync(ChatFrames.Chatline("hello everyone"));

        // The event is an observation, not a gate. Nimbus routes bytes and does not author game
        // content, so the line goes on to the backend whatever any handler thinks of it.
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("hello everyone"),
            "the chat line did not reach the backend");
    }

    [Fact]
    public async Task ASlowChatHandler_DoesNotStallThePlayersOwnTraffic()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        var held = new TaskCompletionSource();
        harness.Events.Subscribe<PlayerChatEvent>(async _ => await held.Task);
        await harness.IdentifyAsync();

        await harness.SendAsync(ChatFrames.Chatline("first line"));
        await harness.SendAsync(ChatFrames.Chatline("second line"));

        // Chat is dispatched off the pump precisely so a plugin talking to Discord cannot hold
        // up the byte stream a player is walking around on.
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("second line"),
            "a blocked chat handler stalled the c->s pump");
        held.SetResult();
    }

    [Fact]
    public async Task APluginThatThrowsOnAChatLine_DoesNotEndTheSession()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        harness.Events.Subscribe<PlayerChatEvent>(_ => throw new InvalidOperationException("handler blew up"));
        await harness.IdentifyAsync();

        await harness.SendAsync(ChatFrames.Chatline("hello"));
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("hello"), "the line never arrived");

        await harness.SendAsync(ChatFrames.Chatline("still here"));
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("still here"),
            "a throwing chat handler took the session down with it");
    }

    // ---- handlers deciding the initial connect ----

    [Fact]
    public async Task AHandlerDenyingTheConnection_SendsThePlayerAwayWithTheReason()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        harness.Events.Subscribe<PlayerConnectEvent>(evt => evt.Deny("the server is closed for maintenance"));

        await harness.SendAsync(ClientFrames.LoginTokenQuery());

        Assert.Equal("the server is closed for maintenance",
            ForgedFrames.Disconnect(await harness.ReadFromProxyAsync()));
        // Denied before any upstream was opened, so the backend never saw them.
        Assert.Equal(0, harness.Backends["hub"].Connections);
    }

    [Fact]
    public async Task AHandlerCancellingTheServerChoice_SendsThePlayerAwayWithTheReason()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        harness.Events.Subscribe<PlayerChooseInitialServerEvent>(evt => evt.Cancel("no room for you today"));

        await harness.SendAsync(ClientFrames.LoginTokenQuery());

        Assert.Equal("no room for you today", ForgedFrames.Disconnect(await harness.ReadFromProxyAsync()));
        Assert.Equal(0, harness.Backends["hub"].Connections);
    }

    [Fact]
    public async Task AHandlerChoosingADifferentBackend_IsObeyed()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        using var elsewhere = SessionHarness.ExtraBackend();
        harness.Events.Subscribe<PlayerChooseInitialServerEvent>(
            evt => evt.Target = elsewhere.Endpoint("elsewhere").ToServerInfo());

        await harness.SendAsync(ClientFrames.LoginTokenQuery());

        // This is how a lobby or queue plugin does its job: the router picked hub, the plugin
        // overrode it, and the override is where the session opens.
        await SessionHarness.WaitForAsync(() => elsewhere.Connections > 0, "the handler's choice was ignored");
        Assert.Equal(0, harness.Backends["hub"].Connections);
    }

    [Fact]
    public async Task AHandlerSwappingTheTargetAtServerPreConnect_IsObeyed()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        using var elsewhere = SessionHarness.ExtraBackend();
        harness.Events.Subscribe<ServerPreConnectEvent>(
            evt => evt.Target = elsewhere.Endpoint("elsewhere").ToServerInfo());

        await harness.SendAsync(ClientFrames.LoginTokenQuery());

        await SessionHarness.WaitForAsync(() => elsewhere.Connections > 0, "the pre-connect swap was ignored");
        Assert.Equal(0, harness.Backends["hub"].Connections);
    }

    [Fact]
    public async Task AHandlerCancellingAtServerPreConnect_StopsTheChainRatherThanFailingOver()
    {
        using var harness = await SessionHarness.StartAsync(
            cfg => cfg.Try = new List<string> { "hub", "spare" }, "hub", "spare");
        harness.Events.Subscribe<ServerPreConnectEvent>(evt => evt.Cancel("not this one"));

        await harness.SendAsync(ClientFrames.LoginTokenQuery());
        var reason = ForgedFrames.Disconnect(await harness.ReadFromProxyAsync());

        // A cancel is a decision, not a failure, so the failover chain does not try the next
        // candidate behind the handler's back.
        Assert.Contains("No backend reachable right now", reason);
        Assert.Equal(0, harness.Backends["hub"].Connections);
        Assert.Equal(0, harness.Backends["spare"].Connections);
    }

    // ---- when no backend will take the player ----

    [Fact]
    public async Task WhenTheFirstBackendIsDown_TheSessionFailsOverToTheNext()
    {
        using var harness = await SessionHarness.StartAsync(cfg =>
        {
            // A backend that is configured but not listening, which is what a crashed one looks
            // like between the crash and the registry noticing.
            cfg.Servers["down"] = $"127.0.0.1:{SessionHarness.DeadEndpoint().Port}";
            cfg.Try = new List<string> { "down", "hub" };
        }, "hub");

        await harness.SendAsync(ClientFrames.LoginTokenQuery());

        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Connections > 0,
            "the session did not fail over to the second candidate");
    }

    [Fact]
    public async Task WhenNoBackendAnswers_ThePlayerIsToldRatherThanDropped()
    {
        using var harness = await SessionHarness.StartAsync(cfg =>
        {
            cfg.Servers = new Dictionary<string, string>
            {
                ["down"] = $"127.0.0.1:{SessionHarness.DeadEndpoint().Port}",
            };
            cfg.Try = new List<string> { "down" };
        }, "unused");

        await harness.SendAsync(ClientFrames.LoginTokenQuery());
        var reason = ForgedFrames.Disconnect(await harness.ReadFromProxyAsync(4000));

        // A dropped socket looks like a network problem on the player's end. Naming the reason is
        // the difference between "my internet is broken" and "the server is down".
        Assert.NotNull(reason);
        Assert.Contains("No backend reachable right now", reason);
        Assert.Contains("Please try again shortly", reason!);
    }

    [Fact]
    public async Task WhenTheRouterHasNoCandidatesAtAll_ThePlayerIsToldRatherThanDropped()
    {
        using var harness = await SessionHarness.StartAsync(cfg =>
        {
            cfg.Servers = new Dictionary<string, string>();
            cfg.Try = new List<string>();
        }, "unused");

        await harness.SendAsync(ClientFrames.LoginTokenQuery());
        var reason = ForgedFrames.Disconnect(await harness.ReadFromProxyAsync(4000));

        Assert.NotNull(reason);
        Assert.Contains("No backend available right now", reason);
    }

    // ---- what plugins are told about the session itself ----

    [Fact]
    public async Task ConnectingToABackend_IsAnnouncedWithNoPreviousServer()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        var connects = new List<ServerPostConnectEvent>();
        harness.Events.Subscribe<ServerPostConnectEvent>(evt => { lock (connects) connects.Add(evt); });

        await harness.IdentifyAsync();

        await SessionHarness.WaitForAsync(() => { lock (connects) return connects.Count > 0; },
            "no post-connect was announced");
        ServerPostConnectEvent evt;
        lock (connects) evt = connects.Single();
        Assert.Equal("hub", evt.Server.ServerId);
        // Null rather than the same backend: this is a join, not a move.
        Assert.Null(evt.Previous);
    }

    [Fact]
    public async Task WhenASessionEnds_PluginsAreToldWithTheBytesItCarried()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        var disconnects = new List<PlayerDisconnectEvent>();
        harness.Events.Subscribe<PlayerDisconnectEvent>(evt => { lock (disconnects) disconnects.Add(evt); });
        await harness.IdentifyAsync("uid-1", "alice");
        // Sent through the pump rather than as the first frame: the frame the runner reads to
        // decide routing is replayed upstream by the connect path, which does not go through the
        // counters, so a session that only ever sent its Identification reports zero.
        var chat = ChatFrames.Chatline("hello");
        await harness.SendAsync(chat);
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("hello"), "the line never arrived");

        harness.Session.Close();

        await SessionHarness.WaitForAsync(() => { lock (disconnects) return disconnects.Count > 0; },
            "no disconnect was announced");
        PlayerDisconnectEvent evt;
        lock (disconnects) evt = disconnects.Single();
        Assert.Equal("alice", evt.Player.Name);
        Assert.Equal("uid-1", evt.Player.Uid);
        Assert.Equal(chat.Length, evt.BytesC2S);
    }

    [Fact]
    public async Task APluginThatThrowsOnDisconnect_DoesNotStopTheSessionTearingDown()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        harness.Events.Subscribe<PlayerDisconnectEvent>(_ => throw new InvalidOperationException("handler blew up"));
        await harness.IdentifyAsync();

        harness.Session.Close();

        // The session is over either way; a handler that throws must not leave it half torn down
        // and holding its sockets.
        await SessionHarness.WaitForAsync(() => harness.Running.IsCompleted,
            "a throwing disconnect handler wedged the session teardown");
    }

    // ---- the byte pumps ----

    [Fact]
    public async Task BytesFlowBothWaysBetweenThePlayerAndTheBackend()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        await harness.IdentifyAsync();
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("uid-1"), "nothing reached the backend");

        await harness.SendAsync(ChatFrames.Chatline("upstream please"));

        Assert.True(await WaitForSent(harness.Backends["hub"], "upstream please"));
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(64 * 1024)]
    public async Task TheConfiguredBufferSize_DoesNotChangeWhatArrives(int bufferSize)
    {
        using var harness = await SessionHarness.StartAsync(cfg => cfg.Advanced.BufferSize = bufferSize, "hub");
        await harness.IdentifyAsync();

        // A chat line longer than the smaller buffer, so the pump has to reassemble across reads
        // without losing or duplicating anything.
        string line = new string('x', 4096);
        await harness.SendAsync(ChatFrames.Chatline(line));

        Assert.True(await WaitForSent(harness.Backends["hub"], line));
    }

    // The player's socket dying stops the c->s pump, but the s->c pump was left blocked on a read
    // from a backend with nothing to say, so the session outlived the player until the backend
    // timed out on its own (#89). The backend here stays open on purpose: if the session only ends
    // because the far end gave up, this waits forever instead of passing.
    [Fact]
    public async Task APlayerDroppingItsSocket_EndsTheSessionWithoutWaitingOnTheBackend()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        await harness.ReachReadyAsync();

        harness.DropPlayerSocket();

        var finished = await Task.WhenAny(harness.Running, Task.Delay(5000));
        Assert.Same(harness.Running, finished);
    }

    // The sibling cancelled by the fix above exits through the same place a backend hanging up
    // does, so without the first-exit record the client leaving looked exactly like the backend
    // kicking the player out.
    [Fact]
    public async Task APlayerDroppingItsSocket_IsNotReportedAsABackendKick()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        var kicks = new List<ServerKickedEvent>();
        harness.Events.Subscribe<ServerKickedEvent>(evt => { lock (kicks) kicks.Add(evt); });
        await harness.ReachReadyAsync();

        harness.DropPlayerSocket();
        Assert.Same(harness.Running, await Task.WhenAny(harness.Running, Task.Delay(5000)));

        lock (kicks) Assert.Empty(kicks);
    }

    // The other half of the same record: a backend that really does hang up still has to be
    // reported, exactly once, rather than being silenced along with the false positive.
    [Fact]
    public async Task ABackendHangingUp_IsStillReportedExactlyOnce()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        var kicks = new List<ServerKickedEvent>();
        harness.Events.Subscribe<ServerKickedEvent>(evt => { lock (kicks) kicks.Add(evt); });
        await harness.ReachReadyAsync();

        harness.Backends["hub"].DropConnections();
        Assert.Same(harness.Running, await Task.WhenAny(harness.Running, Task.Delay(5000)));

        lock (kicks) Assert.Single(kicks);
    }

    // A pump ends on a read from one end or on a write to the other, so which endpoint went away
    // does not follow from which direction the pump ran in. Checked here directly because the
    // socket-level tests below can only ever exercise whichever of the two paths wins the race.
    [Fact]
    public void APumpExit_NamesTheEndpointThatEndedItRatherThanItsOwnDirection()
    {
        Assert.Equal(ProxySession.PumpExitOrigin.Client, ProxySession.EndpointThatEnded(isC2S: true, onRead: true));
        // The one the direction flag used to get wrong: c->s writes the backend, so a failed
        // write there is the backend going away, not the client.
        Assert.Equal(ProxySession.PumpExitOrigin.Backend, ProxySession.EndpointThatEnded(isC2S: true, onRead: false));
        Assert.Equal(ProxySession.PumpExitOrigin.Backend, ProxySession.EndpointThatEnded(isC2S: false, onRead: true));
        // And its mirror: s->c writes the client, so a failed write there is the client leaving.
        Assert.Equal(ProxySession.PumpExitOrigin.Client, ProxySession.EndpointThatEnded(isC2S: false, onRead: false));
    }

    // How many sessions each of the two write-failure scenarios below is run over. Which pump
    // notices a reset first is a property of the kernel, not of the proxy, so a single session
    // proves nothing about the other orderings. The outcome asserted holds for all of them.
    private const int RaceRounds = 25;

    // Both pumps sitting in a write at once, which is what puts the write-failure paths in play:
    // the player is talking to a backend that has stopped draining, and the backend is talking to
    // a player nothing in the harness reads for. In that state a reset on one side is only seen by
    // the pump writing towards it, and the other one is blocked against a socket that is still
    // fine, so the endpoint that failed and the direction of the pump that noticed are opposites.
    private static async Task<Task> WedgeBothPumpsInWritesAsync(SessionHarness harness, string label)
    {
        var backend = harness.Backends["hub"];
        backend.StopReading();
        backend.KeepSending();
        var traffic = harness.KeepPlayerSendingAsync();
        await SessionHarness.WaitForStallAsync(() => harness.PlayerBytesSent,
            $"{label}: the player's stream never backed up at the backend");
        await SessionHarness.WaitForStallAsync(() => backend.BytesSent,
            $"{label}: the backend's stream never backed up at the client");
        return traffic;
    }

    // A client resetting under a backend that is mid-send ends the s->c pump on its write, and
    // s->c is the direction that used to be read as "the backend hung up". The player is the one
    // who left, so no plugin should be told the server kicked them.
    [Fact]
    public async Task AClientResettingUnderBackendTraffic_IsNotReportedAsABackendKick()
    {
        for (int round = 0; round < RaceRounds; round++)
        {
            using var harness = await SessionHarness.StartAsync("hub");
            var kicks = new List<ServerKickedEvent>();
            harness.Events.Subscribe<ServerKickedEvent>(evt => { lock (kicks) kicks.Add(evt); });
            await harness.ReachReadyAsync();
            var traffic = await WedgeBothPumpsInWritesAsync(harness, $"round {round}");

            harness.AbortPlayerSocket();

            Assert.Same(harness.Running, await Task.WhenAny(harness.Running, Task.Delay(5000)));
            lock (kicks) Assert.Empty(kicks);
            await traffic;
        }
    }

    // The mirror of it: a backend resetting under a player who is mid-send ends the c->s pump on
    // its write, and c->s is the direction that used to be read as "the client left" and silenced
    // the kick. The backend is the one that went away, so it is reported, once.
    [Fact]
    public async Task ABackendResettingUnderClientTraffic_IsStillReportedExactlyOnce()
    {
        for (int round = 0; round < RaceRounds; round++)
        {
            using var harness = await SessionHarness.StartAsync("hub");
            var kicks = new List<ServerKickedEvent>();
            harness.Events.Subscribe<ServerKickedEvent>(evt => { lock (kicks) kicks.Add(evt); });
            await harness.ReachReadyAsync();
            var traffic = await WedgeBothPumpsInWritesAsync(harness, $"round {round}");

            harness.Backends["hub"].AbortConnections();

            Assert.Same(harness.Running, await Task.WhenAny(harness.Running, Task.Delay(5000)));
            lock (kicks) Assert.Single(kicks);
            await traffic;
        }
    }

    // RetireSwapPredecessorAsync gives the old pumps a bounded wait and then commits anyway, and
    // an old c->s pump parked in MintReservationAsync is not listening to the predecessor's
    // cancellation: that call waits on the session token. When it finally came back it exited
    // through the same accounting as the pair that had replaced it, claimed the new record and
    // cancelled the new pumps, which took the backend the player had just been moved to down.
    // The wait in PumpUntilClosedAsync had the same blind spot: the pair it finished waiting on
    // was the old one, and with the swap long over it read that as the session being finished.
    [Fact]
    public async Task APredecessorPumpOutlivingItsRetirement_LeavesTheNewPairAlone()
    {
        var registry = new FakeRegistryClient { HoldMint = new TaskCompletionSource() };
        using var harness = await SessionHarness.StartAsync(cfg =>
        {
            cfg.Transfers.AllowSeamless = true;
            cfg.Transfers.EnableUnsafeSeamlessSplice = true;
        }, registry, "hub");
        // This session only, so the other suites running alongside keep the production cap.
        harness.Session.RetireWait = TimeSpan.FromMilliseconds(250);
        var kicks = new List<ServerKickedEvent>();
        harness.Events.Subscribe<ServerKickedEvent>(evt => { lock (kicks) kicks.Add(evt); });

        // Opening on LoginTokenQuery leaves the reservation unminted at connect time, which is
        // what puts the mint on the pump instead: the identity only turns up on a later frame.
        await harness.SendAsync(ClientFrames.LoginTokenQuery());
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Connections > 0,
            "the session never reached the backend");

        // Both frames in one write so the sniffer sees the join and the ready in the chunk the
        // pump is about to park on. Identification is what the swap needs to replay, and Ready
        // is the phase a false kick would be reported from.
        await harness.SendAsync([.. ClientFrames.Identification("uid-1", "alice"), .. ClientFrames.ClientPlaying()]);
        await SessionHarness.WaitForAsync(() => registry.MintsSoFar().Count == 1,
            "the c->s pump never reached the reservation mint");
        await SessionHarness.WaitForAsync(() => harness.Session.Phase == SessionState.Phase.Ready,
            $"the session never reached Ready (phase={harness.Session.Phase})");

        // The swap cannot retire that pump, so it gives up waiting and installs the new pair
        // over the top of a predecessor that is still alive.
        Assert.Null(await harness.Session.RequestSeamlessAsync(harness.Endpoint("hub"), failOnRegistryError: false));

        registry.HoldMint.SetResult();

        // The new upstream is the only thing that can carry this, and the session has to still
        // be running to carry it at all.
        await harness.SendAsync(ChatFrames.Chatline("still connected"));
        Assert.True(await WaitForSent(harness.Backends["hub"], "still connected"));
        Assert.False(harness.Running.IsCompleted);
        lock (kicks) Assert.Empty(kicks);
    }

    private static async Task<bool> WaitForSent(RecordingBackend backend, string needle, int millis = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(millis);
        while (DateTime.UtcNow < deadline)
        {
            if (backend.Sent(needle)) return true;
            await Task.Delay(20);
        }
        return false;
    }
}
