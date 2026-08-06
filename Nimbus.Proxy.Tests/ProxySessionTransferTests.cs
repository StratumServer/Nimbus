using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The two ways a live session moves a player: the forged redirect that makes the client
/// reconnect, and the seamless splice that swaps the upstream socket underneath it. Driven over
/// real sockets, so the assertions are on the bytes the client received, the connections the
/// backends accepted and the route left staged for the reconnect.
///
/// The refusals matter as much as the moves. A transfer that half-happens leaves a player on a
/// dead socket, and one that replays the Identification at the wrong backend gets them kicked
/// with "Bad game session, try relogging" (#57).
/// </summary>
public class ProxySessionTransferTests
{
    // ---- redirect ----

    [Fact]
    public async Task ARedirect_WritesTheRedirectFrameStagesTheRouteAndClosesTheSession()
    {
        using var harness = await SessionHarness.StartAsync("hub", "creative");
        await harness.IdentifyAsync("uid-1", "alice");
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("uid-1"), "the join never reached hub");

        var fail = await harness.Session.RequestRedirectAsync(harness.Endpoint("creative"),
            registry: null, reason: "admin swap", failOnRegistryError: false);

        Assert.Null(fail);

        // The client is told where to go, by the display name an operator would recognise.
        var redirect = ForgedFrames.Redirect(await harness.ReadFromProxyAsync());
        Assert.NotNull(redirect);
        Assert.Equal("creative", redirect!.Value.Name);

        // And the reconnect that follows is claimed in advance, under both keys: the uid for a
        // client that identifies first, the address for a stock one whose first frame is a
        // LoginTokenQuery carrying no identity (#57).
        var staged = Assert.Single(harness.Stickies.Snapshot());
        Assert.Equal("uid-1", staged.Uid);
        Assert.Equal(harness.ClientIp, staged.ClientIp);
        Assert.Equal(harness.Backends["creative"].Port, staged.Target.Port);
        Assert.Equal("admin swap", staged.Reason);

        // The session is over: the redirect only works because the client reconnects.
        await SessionHarness.WaitForAsync(() => harness.Running.IsCompleted, "the session stayed open after the redirect");
    }

    [Fact]
    public async Task ARedirect_StampsTheBackendAddressByDefault()
    {
        using var harness = await SessionHarness.StartAsync("hub", "creative");
        await harness.IdentifyAsync();

        await harness.Session.RequestRedirectAsync(harness.Endpoint("creative"), failOnRegistryError: false);

        var redirect = ForgedFrames.Redirect(await harness.ReadFromProxyAsync());
        Assert.Equal($"127.0.0.1:{harness.Backends["creative"].Port}", redirect!.Value.Host);
    }

    [Fact]
    public async Task ARedirect_StampsTheProxyAddressWhenTheOperatorSetOne()
    {
        using var harness = await SessionHarness.StartAsync(
            cfg => cfg.Transfers.RedirectAddress = "play.example.net:42420", "hub", "creative");
        await harness.IdentifyAsync();

        await harness.Session.RequestRedirectAsync(harness.Endpoint("creative"), failOnRegistryError: false);

        // A vanilla client with the redirect crash fixed dials the stamped host literally.
        // Stamping the backend would take it round the proxy and past every gate on it (#18).
        var redirect = ForgedFrames.Redirect(await harness.ReadFromProxyAsync());
        Assert.Equal("play.example.net:42420", redirect!.Value.Host);
    }

    [Fact]
    public async Task ARedirectToAnAddressWithNoServerId_NamesTheAddressToThePlayer()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        using var target = SessionHarness.ExtraBackend();
        await harness.IdentifyAsync();

        await harness.Session.RequestRedirectAsync(target.Endpoint(), failOnRegistryError: false);

        var redirect = ForgedFrames.Redirect(await harness.ReadFromProxyAsync());
        Assert.Equal($"127.0.0.1:{target.Port}", redirect!.Value.Name);
    }

    [Fact]
    public async Task ARedirectBeforeTheClientHasIdentified_IsRefused()
    {
        using var harness = await SessionHarness.StartAsync("hub", "creative");
        await harness.SendAsync(ClientFrames.LoginTokenQuery());

        // Without the captured Identification there is no uid to stage a route under, so the
        // reconnect would land on the default backend and the transfer would silently not happen.
        var fail = await harness.Session.RequestRedirectAsync(harness.Endpoint("creative"), failOnRegistryError: false);

        Assert.StartsWith("no Identification captured yet", fail);
        Assert.Empty(harness.Stickies.Snapshot());
    }

    [Fact]
    public async Task ARedirectOnASessionThatHasAlreadyGone_IsRefused()
    {
        using var harness = await SessionHarness.StartAsync("hub", "creative");
        await harness.IdentifyAsync();
        harness.Session.Close();

        var fail = await harness.Session.RequestRedirectAsync(harness.Endpoint("creative"), failOnRegistryError: false);

        Assert.Equal("session closed", fail);
        Assert.Empty(harness.Stickies.Snapshot());
    }

    [Fact]
    public async Task ARedirectToAnEmptyHost_IsRefused()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        await harness.IdentifyAsync();

        var fail = await harness.Session.RequestRedirectAsync(
            new BackendEndpoint { Host = "", Port = 42421, ServerId = "nowhere" }, failOnRegistryError: false);

        Assert.Equal("redirect target has empty host", fail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public async Task ARedirectToAPortNoClientCouldDial_IsRefused(int port)
    {
        using var harness = await SessionHarness.StartAsync("hub");
        await harness.IdentifyAsync();

        var fail = await harness.Session.RequestRedirectAsync(
            new BackendEndpoint { Host = "127.0.0.1", Port = port, ServerId = "nowhere" }, failOnRegistryError: false);

        Assert.Equal($"redirect target has invalid port {port}", fail);
    }

    [Fact]
    public async Task ARedirectWhoseReservationCannotBeMinted_IsAbandonedRatherThanHalfDone()
    {
        using var harness = await SessionHarness.StartAsync("hub", "creative");
        await harness.IdentifyAsync();
        var registry = new FakeRegistryClient { FailMint = true };

        var fail = await harness.Session.RequestRedirectAsync(harness.Endpoint("creative"), registry,
            reason: "admin swap", failOnRegistryError: true);

        // A redirect without a reservation sends the player to a backend that will turn them away
        // at the door. Better to leave them where they are and say why.
        Assert.Equal("registry mint failed", fail);
        Assert.Empty(harness.Stickies.Snapshot());
        Assert.Null(ForgedFrames.Redirect(await harness.ReadFromProxyAsync(500)));
    }

    [Fact]
    public async Task ARedirectWhoseReservationCannotBeMinted_StillGoesAheadWhenTheOperatorAllowsIt()
    {
        using var harness = await SessionHarness.StartAsync("hub", "creative");
        await harness.IdentifyAsync();
        var registry = new FakeRegistryClient { FailMint = true };

        // registry.fail_on_error = false is the setting for a network that would rather move
        // players on a best-effort basis than stop moving them when the registry is unwell.
        var fail = await harness.Session.RequestRedirectAsync(harness.Endpoint("creative"), registry,
            reason: "admin swap", failOnRegistryError: false);

        Assert.Null(fail);
        Assert.Single(harness.Stickies.Snapshot());
    }

    // ---- the late sticky fallback ----

    [Fact]
    public async Task ARouteStagedWhileTheSessionWasConnecting_StillMovesThePlayer()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        using var target = SessionHarness.ExtraBackend();
        // The stock first frame carries no identity, so the runner cannot match a uid-only route
        // and the session lands on hub. The route is still there when Identification arrives.
        await harness.SendAsync(ClientFrames.LoginTokenQuery());
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Connections > 0, "the session never reached hub");

        harness.Stickies.Stage("uid-1", clientIp: null, target.Endpoint("target"),
            StickyRouteTable.UidTtl, "operator swap");
        await harness.IdentifyAsync("uid-1", "alice");

        // Redirect rather than splice: this session has already spent its mp token on hub, so
        // replaying those bytes at the target would have the target ask the auth server about a
        // token that no longer exists and kick the player (#57).
        var redirect = ForgedFrames.Redirect(await harness.ReadFromProxyAsync(3000));
        Assert.NotNull(redirect);
        Assert.Equal("target", redirect!.Value.Name);
    }

    [Fact]
    public async Task ARouteAlreadyPointingAtTheBackendWeAreOn_IsDroppedRatherThanReplayed()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        await harness.SendAsync(ClientFrames.LoginTokenQuery());
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Connections > 0, "the session never reached hub");

        harness.Stickies.Stage("uid-1", clientIp: null, harness.Endpoint("hub"),
            StickyRouteTable.UidTtl, "already here");
        await harness.IdentifyAsync("uid-1", "alice");
        await Task.Delay(500);

        // Redirecting to where the player already is would cost them a reconnect for nothing, and
        // the replayed Identification would trip the backend's duplicate-login path.
        Assert.Null(ForgedFrames.Redirect(await harness.ReadFromProxyAsync(500)));
        Assert.Equal(1, harness.Backends["hub"].Connections);
    }

    [Fact]
    public async Task ARouteThatHasAlreadyBouncedThePlayerThreeTimes_IsGivenUpOn()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        using var target = SessionHarness.ExtraBackend();
        await harness.SendAsync(ClientFrames.LoginTokenQuery());
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Connections > 0, "the session never reached hub");

        // Three redirects have already fired for this route and the player is still not there.
        // Past that it is a loop, not a retry, and leaving them on a working session beats
        // bouncing them between backends forever.
        harness.Stickies.Stage("uid-1", clientIp: null, target.Endpoint("target"),
            StickyRouteTable.UidTtl, "operator swap", attempts: 3);
        await harness.IdentifyAsync("uid-1", "alice");
        await Task.Delay(500);

        Assert.Null(ForgedFrames.Redirect(await harness.ReadFromProxyAsync(500)));
        Assert.Equal(0, target.Connections);
        // Given up on rather than left to fire again on the next reconnect.
        Assert.Empty(harness.Stickies.Snapshot());
    }

    [Fact]
    public async Task ARouteMatchedOnAnAddressThatTurnedOutToBeSomebodyElse_GoesBackUnderItsOwnUid()
    {
        using var harness = await SessionHarness.StartAsync("hub");
        using var target = SessionHarness.ExtraBackend();

        // Two players behind one NAT. The route was staged for uid-transferring, but uid-other
        // reconnected from the same address first and picked it up.
        harness.Stickies.Stage("uid-transferring", "127.0.0.1", target.Endpoint("target"),
            StickyRouteTable.UidTtl, "operator swap");
        await harness.SendAsync(ClientFrames.LoginTokenQuery());
        await SessionHarness.WaitForAsync(() => target.Connections > 0, "the address-matched route was not taken");

        await harness.IdentifyAsync("uid-other", "bob");
        await SessionHarness.WaitForAsync(() => harness.Stickies.Snapshot().Count > 0,
            "the route was not put back for the player it was staged for");

        var restaged = Assert.Single(harness.Stickies.Snapshot());
        Assert.Equal("uid-transferring", restaged.Uid);
        // Under the uid alone: putting it back on the address index is how the two of them would
        // swap places a second time.
        Assert.Equal("", restaged.ClientIp);
    }

    // ---- seamless ----

    [Fact]
    public async Task ASeamlessSwapToTheBackendTheSessionIsAlreadyOn_ReconnectsUnderneathThePlayer()
    {
        using var harness = await SessionHarness.StartAsync(cfg =>
        {
            cfg.Transfers.AllowSeamless = true;
            cfg.Transfers.EnableUnsafeSeamlessSplice = true;
        }, "hub");
        await harness.IdentifyAsync();
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("uid-1"), "the join never reached hub");

        // The same backend is the one case the replay tripwire allows: those Identification
        // bytes were spent there, and it is the backend that owns the token either way.
        var fail = await harness.Session.RequestSeamlessAsync(harness.Endpoint("hub"),
            swapReason: "plugin transfer", failOnRegistryError: false);

        Assert.Null(fail);
        // A second connection, and the player's own socket was never closed.
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Connections == 2,
            "the swap never opened a new upstream");
        Assert.False(harness.Running.IsCompleted);
    }

    [Fact]
    public async Task AfterASeamlessSwap_ThePlayersBytesGoToTheNewUpstream()
    {
        using var harness = await SessionHarness.StartAsync(cfg =>
        {
            cfg.Transfers.AllowSeamless = true;
            cfg.Transfers.EnableUnsafeSeamlessSplice = true;
        }, "hub");
        await harness.IdentifyAsync();
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("uid-1"), "the join never reached hub");

        Assert.Null(await harness.Session.RequestSeamlessAsync(harness.Endpoint("hub"), failOnRegistryError: false));
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Connections == 2, "no new upstream");

        // The pumps have to be running again on the new socket. Without that the player is on a
        // connection that looks alive and carries nothing.
        await harness.SendAsync(ChatFrames.Chatline("still here"));
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("still here"),
            "the player's traffic stopped reaching the backend after the swap");
    }

    [Fact]
    public async Task ASeamlessSwapToADifferentBackend_IsRefusedBecauseTheTokenIsSpent()
    {
        using var harness = await SessionHarness.StartAsync(cfg => cfg.Transfers.AllowSeamless = true,
            "hub", "creative");
        await harness.IdentifyAsync();
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("uid-1"), "the join never reached hub");

        var fail = await harness.Session.RequestSeamlessAsync(harness.Endpoint("creative"),
            failOnRegistryError: false);

        // The mp token inside the captured Identification is single use. The auth server answers
        // 'missingaccount' the second time it is asked, and the player is kicked with "Bad game
        // session, try relogging" (#57).
        Assert.Contains("Identification was already delivered", fail);
        Assert.Contains("already-consumed mp token", fail);
        Assert.Equal(0, harness.Backends["creative"].Connections);
    }

    [Fact]
    public async Task ASeamlessRefusal_LeavesTheSessionAbleToTransferAgain()
    {
        using var harness = await SessionHarness.StartAsync(cfg => cfg.Transfers.AllowSeamless = true,
            "hub", "creative");
        await harness.IdentifyAsync();
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("uid-1"), "the join never reached hub");

        await harness.Session.RequestSeamlessAsync(harness.Endpoint("creative"), failOnRegistryError: false);

        // The swap lock has to be released on every refusal, or one bad transfer wedges the
        // session against every later one.
        var second = await harness.Session.RequestSeamlessAsync(harness.Endpoint("creative"), failOnRegistryError: false);
        Assert.Contains("Identification was already delivered", second);
    }

    [Fact]
    public async Task ASecondSeamlessWhileTheFirstIsStillInFlight_IsRefusedRatherThanRaced()
    {
        using var harness = await SessionHarness.StartAsync(cfg =>
        {
            cfg.Transfers.AllowSeamless = true;
            cfg.Transfers.EnableUnsafeSeamlessSplice = true;
        }, "hub");
        await harness.IdentifyAsync();
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("uid-1"), "the join never reached hub");

        // A handler holds the first swap open at ServerPreConnect, which is past the point the
        // swap flag goes up.
        var held = new TaskCompletionSource();
        var reached = new TaskCompletionSource();
        harness.Events.Subscribe<ServerPreConnectEvent>(async _ =>
        {
            reached.TrySetResult();
            await held.Task;
        });

        var first = harness.Session.RequestSeamlessAsync(harness.Endpoint("hub"), failOnRegistryError: false);
        await reached.Task;

        // Two swaps at once would tear down one set of pumps while the other installed its own,
        // and the player's stream would be spliced out of two backends' output.
        var second = await harness.Session.RequestSeamlessAsync(harness.Endpoint("hub"), failOnRegistryError: false);
        Assert.Equal("seamless already in progress", second);

        held.SetResult();
        Assert.Null(await first);
    }

    [Fact]
    public async Task ASeamlessSwapAHandlerCancelled_LeavesThePlayerWhereTheyAre()
    {
        using var harness = await SessionHarness.StartAsync(cfg =>
        {
            cfg.Transfers.AllowSeamless = true;
            cfg.Transfers.EnableUnsafeSeamlessSplice = true;
        }, "hub");
        await harness.IdentifyAsync();
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("uid-1"), "the join never reached hub");

        harness.Events.Subscribe<ServerPreConnectEvent>(evt => evt.Cancel("not during the event"));

        var fail = await harness.Session.RequestSeamlessAsync(harness.Endpoint("hub"), failOnRegistryError: false);

        Assert.Equal("cancelled: not during the event", fail);
        Assert.Equal(1, harness.Backends["hub"].Connections);
        Assert.False(harness.Running.IsCompleted);
    }

    [Fact]
    public async Task ASeamlessSwapToABackendThatIsNotListening_LeavesThePlayerOnTheOldOne()
    {
        using var harness = await SessionHarness.StartAsync(cfg =>
        {
            cfg.Transfers.AllowSeamless = true;
            cfg.Transfers.EnableUnsafeSeamlessSplice = true;
        }, "hub");
        await harness.IdentifyAsync();
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("uid-1"), "the join never reached hub");

        var fail = await harness.Session.RequestSeamlessAsync(SessionHarness.DeadEndpoint(), failOnRegistryError: false);

        // The old upstream was never touched, so a failed swap costs the player nothing.
        Assert.StartsWith("connect failed:", fail);
        Assert.False(harness.Running.IsCompleted);
        await harness.SendAsync(ChatFrames.Chatline("still connected"));
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("still connected"),
            "the failed swap took the player's working session with it");
    }

    [Fact]
    public async Task ASeamlessSwapBeforeTheClientHasIdentified_IsRefused()
    {
        using var harness = await SessionHarness.StartAsync(cfg => cfg.Transfers.AllowSeamless = true, "hub");
        await harness.SendAsync(ClientFrames.LoginTokenQuery());

        // There would be no Identification to replay at the new backend, so the swap would land
        // the player on a socket the backend never authenticated.
        var fail = await harness.Session.RequestSeamlessAsync(harness.Endpoint("hub"), failOnRegistryError: false);

        Assert.StartsWith("no Identification captured yet", fail);
    }

    [Fact]
    public async Task ASeamlessSwapOnAClosedSession_IsRefused()
    {
        using var harness = await SessionHarness.StartAsync(cfg => cfg.Transfers.AllowSeamless = true, "hub");
        await harness.IdentifyAsync();
        harness.Session.Close();

        Assert.Equal("session closed",
            await harness.Session.RequestSeamlessAsync(harness.Endpoint("hub"), failOnRegistryError: false));
    }

    // ---- mode selection ----

    [Fact]
    public async Task SeamlessWithSeamlessTurnedOff_IsRefusedRatherThanQuietlyRedirected()
    {
        using var harness = await SessionHarness.StartAsync("hub", "creative");
        await harness.IdentifyAsync();

        var (mode, fail) = await harness.Session.RequestTransferAsync(harness.Endpoint("creative"), "seamless",
            failOnRegistryError: false);

        Assert.Equal("seamless", mode);
        Assert.Equal("seamless transfers disabled in config", fail);
    }

    [Fact]
    public async Task AnUnknownTransferMode_IsNamedBackRatherThanGuessedAt()
    {
        using var harness = await SessionHarness.StartAsync("hub", "creative");
        await harness.IdentifyAsync();

        var (mode, fail) = await harness.Session.RequestTransferAsync(harness.Endpoint("creative"), "teleport",
            failOnRegistryError: false);

        Assert.Equal("teleport", mode);
        Assert.Equal("unknown transfer mode 'teleport'", fail);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("REDIRECT")]
    public async Task ARedirectAskedForByName_GoesDownTheRedirectPath(string mode)
    {
        using var harness = await SessionHarness.StartAsync("hub", "creative");
        await harness.IdentifyAsync();

        var (used, fail) = await harness.Session.RequestTransferAsync(harness.Endpoint("creative"), mode,
            failOnRegistryError: false);

        Assert.Equal("redirect", used);
        Assert.Null(fail);
        Assert.Single(harness.Stickies.Snapshot());
    }

    [Fact]
    public async Task SeamlessAskedForBeforeThePlayerHasFinishedJoining_IsRefusedWithThePhase()
    {
        using var harness = await SessionHarness.StartAsync(cfg =>
        {
            cfg.Transfers.AllowSeamless = true;
            cfg.Transfers.RequireSeamlessCapability = false;
        }, "hub", "creative");
        await harness.IdentifyAsync();

        // Splicing a session that is still handshaking hands the new backend a client mid-login.
        var (_, fail) = await harness.Session.RequestTransferAsync(harness.Endpoint("creative"), "seamless",
            failOnRegistryError: false);

        Assert.StartsWith("seamless requires a fully joined session", fail);
        Assert.Contains("current phase=", fail);
    }

    [Fact]
    public async Task SeamlessForAClientWithoutTheNimbusMod_FallsBackToARedirect()
    {
        using var harness = await SessionHarness.StartAsync(cfg =>
        {
            cfg.Transfers.AllowSeamless = true;
            cfg.Transfers.RequireSeamlessCapability = true;
            cfg.Transfers.FallbackToRedirectWhenSeamlessUnavailable = true;
        }, "hub", "creative");
        await harness.ReachReadyAsync();

        var (mode, fail) = await harness.Session.RequestTransferAsync(harness.Endpoint("creative"), "seamless",
            failOnRegistryError: false);

        // Most of a network's players are on stock clients. Refusing them would make a seamless
        // default mean "transfers do not work for most people", so the mode falls back and says
        // which one it used.
        Assert.Equal("redirect", mode);
        Assert.Null(fail);
        Assert.Single(harness.Stickies.Snapshot());
        Assert.NotNull(ForgedFrames.Redirect(await harness.ReadFromProxyAsync()));
    }

    [Fact]
    public async Task SeamlessForAClientWithoutTheNimbusMod_IsRefusedWhenTheOperatorTurnedTheFallbackOff()
    {
        using var harness = await SessionHarness.StartAsync(cfg =>
        {
            cfg.Transfers.AllowSeamless = true;
            cfg.Transfers.RequireSeamlessCapability = true;
            cfg.Transfers.FallbackToRedirectWhenSeamlessUnavailable = false;
        }, "hub", "creative");
        await harness.ReachReadyAsync();

        var (mode, fail) = await harness.Session.RequestTransferAsync(harness.Endpoint("creative"), "seamless",
            failOnRegistryError: false);

        Assert.Equal("seamless", mode);
        Assert.Equal("client has not advertised Nimbus seamless capability", fail);
        Assert.Empty(harness.Stickies.Snapshot());
    }

    [Fact]
    public async Task SeamlessForACapableClientWithoutTheUnsafeFlag_IsARedirectWithTheLoadingScreenHidden()
    {
        using var harness = await SessionHarness.StartAsync(cfg =>
        {
            cfg.Transfers.AllowSeamless = true;
            cfg.Transfers.RequireSeamlessCapability = true;
        }, "hub", "creative");
        await harness.ReachReadyAsync();
        harness.Session.MarkSeamlessCapable();

        var (mode, fail) = await harness.Session.RequestTransferAsync(harness.Endpoint("creative"), "seamless",
            failOnRegistryError: false);

        // This is the production seamless path: the same safe redirect underneath, with the
        // Nimbus client hiding the vanilla loading UI. The raw upstream splice stays behind the
        // unsafe flag because it replays a spent mp token.
        Assert.Equal("seamless", mode);
        Assert.Null(fail);
        Assert.Equal("seamless visual redirect", Assert.Single(harness.Stickies.Snapshot()).Reason);
        Assert.NotNull(ForgedFrames.Redirect(await harness.ReadFromProxyAsync()));
        Assert.Equal(0, harness.Backends["creative"].Connections);
    }

    [Theory]
    [InlineData("seamless")]
    [InlineData("splice")]
    [InlineData("SPLICE")]
    public async Task TheLegacySpliceName_ReachesTheSamePathAsSeamless(string mode)
    {
        using var harness = await SessionHarness.StartAsync(cfg =>
        {
            cfg.Transfers.AllowSeamless = true;
            cfg.Transfers.RequireSeamlessCapability = true;
        }, "hub", "creative");
        await harness.ReachReadyAsync();
        harness.Session.MarkSeamlessCapable();

        var (used, fail) = await harness.Session.RequestTransferAsync(harness.Endpoint("creative"), mode,
            failOnRegistryError: false);

        // "splice" is what the mode was called before the rename, and operators' scripts and
        // plugins still say it.
        Assert.Equal("seamless", used);
        Assert.Null(fail);
    }

    // ---- what the plugins are told ----

    [Fact]
    public async Task ACompletedRedirect_IsAnnouncedToPlugins()
    {
        using var harness = await SessionHarness.StartAsync("hub", "creative");
        var transfers = new List<PlayerTransferredEvent>();
        harness.Events.Subscribe<PlayerTransferredEvent>(evt => transfers.Add(evt));
        await harness.IdentifyAsync("uid-1", "alice");
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("uid-1"), "the join never reached hub");

        await harness.Session.RequestRedirectAsync(harness.Endpoint("creative"), failOnRegistryError: false);

        var evt = Assert.Single(transfers);
        Assert.Equal("redirect", evt.Mode);
        Assert.Equal("creative", evt.To.ServerId);
        Assert.Equal("hub", evt.From?.ServerId);
        Assert.Equal("alice", evt.Player.Name);
    }

    [Fact]
    public async Task ACompletedSeamlessSwap_IsAnnouncedToPlugins()
    {
        using var harness = await SessionHarness.StartAsync(cfg =>
        {
            cfg.Transfers.AllowSeamless = true;
            cfg.Transfers.EnableUnsafeSeamlessSplice = true;
        }, "hub");
        var transfers = new List<PlayerTransferredEvent>();
        var postConnects = new List<ServerPostConnectEvent>();
        harness.Events.Subscribe<PlayerTransferredEvent>(evt => transfers.Add(evt));
        harness.Events.Subscribe<ServerPostConnectEvent>(evt => postConnects.Add(evt));
        await harness.IdentifyAsync();
        await SessionHarness.WaitForAsync(() => harness.Backends["hub"].Sent("uid-1"), "the join never reached hub");
        postConnects.Clear();

        await harness.Session.RequestSeamlessAsync(harness.Endpoint("hub"), failOnRegistryError: false);

        Assert.Equal("seamless", Assert.Single(transfers).Mode);
        // Both fire: a plugin tracking where players are needs the post-connect, one keeping a
        // transfer log needs the transfer.
        Assert.Single(postConnects);
    }

    [Fact]
    public async Task APluginThatThrowsOnATransferNotification_DoesNotUndoTheTransfer()
    {
        using var harness = await SessionHarness.StartAsync("hub", "creative");
        harness.Events.Subscribe<PlayerTransferredEvent>(_ => throw new InvalidOperationException("handler blew up"));
        await harness.IdentifyAsync();

        var fail = await harness.Session.RequestRedirectAsync(harness.Endpoint("creative"), failOnRegistryError: false);

        // The redirect packet is already on the wire by then. Reporting it as failed would be a
        // lie the operator would act on.
        Assert.Null(fail);
    }
}
