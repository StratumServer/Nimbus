using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The ban verbs as an operator reaches them: over the admin socket, against a proxy holding real
/// sessions. What is pinned here is what an operator acts on, the parts a wrong answer costs
/// somebody: which uid the ban was recorded against, who got taken off the network, and what the
/// command says when the registry will not play along.
/// </summary>
public class AdminBanCommandTests
{
    private const string Uid = "uid-griefer";

    private static NetworkBan Ban(string uid = Uid, string serverId = "", string name = "griefer",
        long expiresAtUnix = 0) => new()
    {
        PlayerUid = uid,
        PlayerName = name,
        ServerId = serverId,
        Reason = "griefing",
        BannedBy = "admin",
        CreatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ExpiresAtUnix = expiresAtUnix,
    };

    [Fact]
    public async Task ABanByUid_IsRecordedAndTakesThePlayerOffTheNetwork()
    {
        await using var harness = await AdminHarness.StartAsync();
        var player = await harness.JoinAsync(Uid, "griefer");
        harness.Registry.AddBanResult = Ban();

        var reply = await harness.RunAsync(new { cmd = "ban", uid = Uid, reason = "griefing" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(Uid, reply.GetProperty("uid").GetString());
        Assert.Equal("network", reply.GetProperty("scope").GetString());
        Assert.Equal(1, reply.GetProperty("kicked").GetInt32());
        Assert.True(await player.DroppedAsync());

        // The ban that reached the registry carries the operator's reason and is stamped as an
        // admin action, which is what a later audit of the ban list reads.
        var recorded = Assert.IsType<BanRequest>(harness.Registry.LastBanRequest);
        Assert.Equal(Uid, recorded.PlayerUid);
        Assert.Equal("griefing", recorded.Reason);
        Assert.Equal("admin", recorded.BannedBy);
        Assert.Equal("", recorded.ServerId);
    }

    [Fact]
    public async Task ABanByPlayerName_ResolvesTheUidFromTheLiveSession()
    {
        await using var harness = await AdminHarness.StartAsync();
        var player = await harness.JoinAsync(Uid, "griefer");
        harness.Registry.AddBanResult = Ban();

        // Operators know names, and the ban has to land on the uid behind the name or it covers
        // whoever takes that name next instead.
        var reply = await harness.RunAsync(new { cmd = "ban", player = "griefer" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        var recorded = Assert.IsType<BanRequest>(harness.Registry.LastBanRequest);
        Assert.Equal(Uid, recorded.PlayerUid);
        Assert.Equal("griefer", recorded.PlayerName);
        Assert.True(await player.DroppedAsync());
    }

    [Fact]
    public async Task APlayerNameIsMatchedWithoutRegardToCase()
    {
        await using var harness = await AdminHarness.StartAsync();
        await harness.JoinAsync(Uid, "Griefer");
        harness.Registry.AddBanResult = Ban();

        var reply = await harness.RunAsync(new { cmd = "ban", player = "griefer" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(Uid, Assert.IsType<BanRequest>(harness.Registry.LastBanRequest).PlayerUid);
    }

    [Fact]
    public async Task ABanByNameForSomebodyNotConnected_SendsTheOperatorToTheUidForm()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.AddBanResult = Ban();

        var reply = await harness.RunAsync(new { cmd = "ban", player = "ghost" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("no live session for player 'ghost'", reply.GetProperty("reason").GetString());
        // Nothing reached the registry, so a typo'd name cannot ban an empty uid.
        Assert.Null(harness.Registry.LastBanRequest);
    }

    [Fact]
    public async Task ABanWithNeitherUidNorName_AnswersWithTheUsageLine()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "ban" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("need either uid or player", reply.GetProperty("reason").GetString());
        Assert.Contains("--uid", reply.GetProperty("usage").GetString());
        Assert.Null(harness.Registry.LastBanRequest);
    }

    [Fact]
    public async Task ANetworkWideBan_TakesEverySessionThatPlayerHolds()
    {
        await using var harness = await AdminHarness.StartAsync();
        var first = await harness.JoinAsync(Uid, "griefer");
        var second = await harness.JoinAsync(Uid, "griefer");
        var bystander = await harness.JoinAsync("uid-innocent", "builder");
        harness.Registry.AddBanResult = Ban();

        var reply = await harness.RunAsync(new { cmd = "ban", uid = Uid });

        // Both of the banned player's sessions, and only theirs. A ban that leaves one of two
        // connections alive is a ban the player can sit out.
        Assert.Equal(2, reply.GetProperty("kicked").GetInt32());
        Assert.True(await first.DroppedAsync());
        Assert.True(await second.DroppedAsync());
        Assert.False(await bystander.DroppedAsync(500));
    }

    [Fact]
    public async Task ABanScopedToAnotherBackend_LeavesThePlayerWhereTheyAre()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        var player = await harness.JoinAsync(Uid, "griefer");
        harness.Registry.AddBanResult = Ban(serverId: "creative");

        var reply = await harness.RunAsync(new { cmd = "ban", uid = Uid, serverId = "creative" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        // The scope the operator typed has to reach the registry, or a ban meant for one backend
        // is stored network-wide and locks the player out of everything.
        Assert.Equal("creative", Assert.IsType<BanRequest>(harness.Registry.LastBanRequest).ServerId);
        Assert.Equal("creative", reply.GetProperty("scope").GetString());
        // The player is on hub, which this ban says nothing about, so the ban records without
        // disturbing the session.
        Assert.Equal(0, reply.GetProperty("kicked").GetInt32());
        Assert.False(await player.DroppedAsync(500));
        Assert.True(harness.Proxy.Sessions.ContainsKey(player.Id));
    }

    [Fact]
    public async Task ABanScopedToTheBackendThePlayerIsOn_TakesThatSession()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "creative" });
        var player = await harness.JoinAsync(Uid, "griefer");
        harness.Registry.AddBanResult = Ban(serverId: "hub");

        var reply = await harness.RunAsync(new { cmd = "ban", uid = Uid, serverId = "hub" });

        Assert.Equal("hub", Assert.IsType<BanRequest>(harness.Registry.LastBanRequest).ServerId);
        Assert.Equal("hub", reply.GetProperty("scope").GetString());
        Assert.Equal(1, reply.GetProperty("kicked").GetInt32());
        Assert.True(await player.DroppedAsync());
    }

    [Fact]
    public async Task ABanWithADuration_CarriesTheSecondsAndReportsTheExpiry()
    {
        await using var harness = await AdminHarness.StartAsync();
        // Deliberately not now+3600: an expiry the command could have computed itself from the
        // duration would not tell a pass-through apart from a recomputation.
        const long expires = 1893456000;
        harness.Registry.AddBanResult = Ban(expiresAtUnix: expires);

        var reply = await harness.RunAsync(new { cmd = "ban", uid = Uid, duration = 3600 });

        Assert.Equal(3600, Assert.IsType<BanRequest>(harness.Registry.LastBanRequest).DurationSeconds);
        // The expiry echoed back is the registry's, not the one the operator asked for: the
        // registry stamps it, and an operator reading "until" wants the stamped value.
        Assert.Equal(expires, reply.GetProperty("expiresAtUnix").GetInt64());
    }

    [Fact]
    public async Task ARegistryThatRefusesTheBan_LeavesThePlayerConnected()
    {
        await using var harness = await AdminHarness.StartAsync();
        var player = await harness.JoinAsync(Uid, "griefer");
        harness.Registry.AddBanResult = null;

        var reply = await harness.RunAsync(new { cmd = "ban", uid = Uid });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("registry refused the ban", reply.GetProperty("reason").GetString());
        // No ban was recorded, so kicking anyone would be acting on a decision nothing holds.
        Assert.False(await player.DroppedAsync(500));
    }

    [Fact]
    public async Task ABanIsPushedIntoTheGateCacheBeforeTheCommandAnswers()
    {
        await using var harness = await AdminHarness.StartAsync();
        await harness.JoinAsync(Uid, "griefer");
        var ban = Ban();
        harness.Registry.AddBanResult = ban;
        harness.Registry.Bans = new List<NetworkBan> { ban };
        Assert.Null(harness.Proxy.Bans.FindBlocking(Uid));

        await harness.RunAsync(new { cmd = "ban", uid = Uid });

        // Without this the player could reconnect in the window before the next poll, which is
        // exactly the window they would use.
        Assert.NotNull(harness.Proxy.Bans.FindBlocking(Uid));
    }

    [Fact]
    public async Task ACacheRefreshThatFails_StillLeavesTheBanRecordedAndThePlayerKicked()
    {
        await using var harness = await AdminHarness.StartAsync();
        var player = await harness.JoinAsync(Uid, "griefer");
        harness.Registry.AddBanResult = Ban();
        // The ban itself landed; it is the follow-up refresh that cannot reach the registry.
        harness.Registry.Throw = true;

        var reply = await harness.RunAsync(new { cmd = "ban", uid = Uid });

        // The kick reads the ban the registry just returned rather than the cache, so a failed
        // refresh costs the reconnect gate a poll and nothing else.
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(1, reply.GetProperty("kicked").GetInt32());
        Assert.True(await player.DroppedAsync());
    }

    [Fact]
    public async Task TheBanList_ReportsWhatTheRegistryHolds()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.Bans = new List<NetworkBan>
        {
            Ban(),
            Ban(uid: "uid-other", serverId: "creative", name: "someone"),
        };

        var reply = await harness.RunAsync(new { cmd = "bans" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(2, reply.GetProperty("count").GetInt32());
        var listed = reply.GetProperty("bans").EnumerateArray().ToList();
        Assert.Equal("network", listed[0].GetProperty("scope").GetString());
        Assert.Equal("creative", listed[1].GetProperty("scope").GetString());
        Assert.Equal("griefing", listed[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task TheBanList_WhenTheRegistryIsUnreachable_SaysSoRatherThanShowingNothing()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.Bans = null;

        var reply = await harness.RunAsync(new { cmd = "bans" });

        // An empty list and an unanswered one read the same on screen, and only one of them
        // means nobody is banned.
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("registry unreachable", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AnUnban_LiftsTheNetworkEntryAndWarmsTheGateCache()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.Bans = new List<NetworkBan> { Ban() };
        await harness.Proxy.Bans.RefreshAsync();
        Assert.NotNull(harness.Proxy.Bans.FindBlocking(Uid));

        harness.Registry.LiftBanResult = true;
        harness.Registry.Bans = new List<NetworkBan>();
        var reply = await harness.RunAsync(new { cmd = "unban", uid = Uid });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("network", reply.GetProperty("scope").GetString());
        Assert.Equal(Uid, harness.Registry.LastLift!.Value.Uid);
        Assert.Equal("", harness.Registry.LastLift!.Value.ServerId);
        // The pardoned player can come back now rather than after the next poll.
        Assert.Null(harness.Proxy.Bans.FindBlocking(Uid));
    }

    [Fact]
    public async Task AnUnbanWithAServerId_LiftsThatScopeRatherThanTheNetworkOne()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.LiftBanResult = true;

        var reply = await harness.RunAsync(new { cmd = "unban", uid = Uid, serverId = "creative" });

        Assert.Equal("creative", reply.GetProperty("scope").GetString());
        Assert.Equal(Uid, harness.Registry.LastLift!.Value.Uid);
        Assert.Equal("creative", harness.Registry.LastLift!.Value.ServerId);
    }

    [Fact]
    public async Task AnUnbanTheRegistryFindsNothingFor_ReportsThatItLiftedNothing()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.LiftBanResult = false;

        var reply = await harness.RunAsync(new { cmd = "unban", uid = "uid-never-banned" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        // The operator still gets told which uid and which scope came back empty, so a typo in
        // the uid reads as a typo rather than as "the ban is gone".
        Assert.Equal("uid-never-banned", reply.GetProperty("uid").GetString());
        Assert.Equal("network", reply.GetProperty("scope").GetString());
        Assert.Equal(("uid-never-banned", ""), harness.Registry.LastLift);
        // Nothing was lifted, so there is nothing for the gate cache to catch up on and no
        // reason to spend a registry round trip refreshing it.
        Assert.Equal(0, harness.Registry.BanFetches);
    }

    [Fact]
    public async Task AnUnbanWithNoUid_AnswersWithTheUsageLine()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "unban" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("missing 'uid'", reply.GetProperty("reason").GetString());
        Assert.Contains("unban --uid", reply.GetProperty("usage").GetString());
        Assert.Null(harness.Registry.LastLift);
    }

    [Fact]
    public async Task WithTheRegistryDisabled_EveryBanVerbSaysWhyItCannotRun()
    {
        await using var harness = await AdminHarness.StartAsync(withRegistry: false);
        var player = await harness.JoinAsync(Uid, "griefer");

        foreach (var payload in new object[]
                 {
                     new { cmd = "ban", uid = Uid },
                     new { cmd = "unban", uid = Uid },
                     new { cmd = "bans" },
                 })
        {
            var reply = await harness.RunAsync(payload);
            Assert.False(reply.GetProperty("ok").GetBoolean());
            Assert.Equal("bans need a registry (registry.mode is 'disabled')",
                reply.GetProperty("reason").GetString());
        }

        // And nobody was kicked on the way to that answer.
        Assert.False(await player.DroppedAsync(500));
    }
}
