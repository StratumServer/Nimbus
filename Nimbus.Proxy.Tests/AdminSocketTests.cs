using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The admin socket itself: the auth handshake that stands between a shell on the box and every
/// command behind it, the permission gate, and the dispatch envelope. Driven over a real TCP
/// connection because the line framing and the closing of the socket after a bad secret are part
/// of what is being tested.
/// </summary>
public class AdminSocketTests
{
    private const string Secret = "operator-secret";

    [Fact]
    public async Task WithNoSecretConfigured_ACommandIsAnsweredWithoutAHandshake()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "ping" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task WithASecretConfigured_ACommandSentBeforeAuth_IsRefusedAndTheSocketClosed()
    {
        await using var harness = await AdminHarness.StartAsync(cfg => cfg.Admin.Secret = Secret);

        using var conn = await harness.ConnectAsync();
        var reply = await conn.SendRawAsync("""{"cmd":"ping"}""");

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("auth required", reply.GetProperty("reason").GetString());
        // The refusal ends the connection: an unauthenticated caller gets one attempt, not a
        // socket to keep guessing on.
        Assert.True(await conn.ClosedByProxyAsync());
    }

    [Fact]
    public async Task TheWrongSecret_IsRefusedAndTheSocketClosed()
    {
        await using var harness = await AdminHarness.StartAsync(cfg => cfg.Admin.Secret = Secret);

        using var conn = await harness.ConnectAsync();
        var reply = await conn.SendAsync(new { cmd = "auth", secret = "not-the-secret" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.True(await conn.ClosedByProxyAsync());
    }

    [Fact]
    public async Task ASecretOfTheWrongLength_IsRefusedLikeAnyOtherWrongSecret()
    {
        await using var harness = await AdminHarness.StartAsync(cfg => cfg.Admin.Secret = Secret);

        // The compare is constant-time, which also means it refuses on a length mismatch rather
        // than leaking the expected length through an early accept. A length check that bailed
        // out before the compare would be free to answer differently, so what is pinned here is
        // that all three refusals are the same bytes and end the same way.
        var refusals = new List<string?>();
        var closed = new List<bool>();
        foreach (string attempt in new[] { Secret[..5], Secret + "-and-more", "wrong" })
        {
            using var conn = await harness.ConnectAsync();
            refusals.Add(await conn.SendRawForLineAsync(
                JsonSerializer.Serialize(new { cmd = "auth", secret = attempt })));
            closed.Add(await conn.ClosedByProxyAsync());
        }

        Assert.Single(refusals.Distinct());
        Assert.Contains("auth required", refusals[0]);
        Assert.DoesNotContain(closed, open => !open);
    }

    [Fact]
    public async Task EveryFailedFirstFrame_GetsTheSameRefusal()
    {
        await using var harness = await AdminHarness.StartAsync(cfg => cfg.Admin.Secret = Secret);

        // A caller probing the socket must not be able to tell "auth is configured but you got
        // the secret wrong" from "that frame was not an auth frame at all", so all four of these
        // come back byte for byte identical.
        var refusals = new List<string?>();
        foreach (var first in new[]
                 {
                     """{"cmd":"auth","secret":"wrong"}""",
                     """{"cmd":"ping"}""",
                     """{"cmd":"auth"}""",
                     "not json at all",
                 })
        {
            using var conn = await harness.ConnectAsync();
            refusals.Add(await conn.SendRawForLineAsync(first));
        }

        Assert.Single(refusals.Distinct());
        Assert.Contains("auth required", refusals[0]);
    }

    [Fact]
    public async Task TheRightSecret_OpensAConnectionThatKeepsServingCommands()
    {
        await using var harness = await AdminHarness.StartAsync(cfg => cfg.Admin.Secret = Secret);

        using var conn = await harness.ConnectAsync();
        var authed = await conn.SendAsync(new { cmd = "auth", secret = Secret });
        Assert.True(authed.GetProperty("ok").GetBoolean());
        Assert.True(authed.GetProperty("authed").GetBoolean());

        // One handshake, then as many commands as the operator wants on the same socket.
        Assert.True((await conn.SendAsync(new { cmd = "ping" })).GetProperty("ok").GetBoolean());
        Assert.True((await conn.SendAsync(new { cmd = "list" })).TryGetProperty("sessions", out _));
    }

    [Fact]
    public async Task WithAdminDisabled_ThePortIsNeverOpened()
    {
        // Not a permission decision but the kill switch above it: admin.enabled = false means
        // nothing is listening at all.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var cfg = new ProxyConfig();
        cfg.Admin.Enabled = false;
        cfg.Admin.Bind = $"127.0.0.1:{port}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var proxy = new ProxyListener(cfg, cts.Token);

        // Returns rather than blocking on accept, and leaves the port closed behind it.
        await new AdminListener(cfg, proxy, cts.Token, () => Array.Empty<LoadedPlugin>()).RunAsync();

        using var tcp = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(
            async () => await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token));
    }

    [Fact]
    public async Task ACommandOutsideTheGrantedPermissions_IsRefusedAndNamed()
    {
        await using var harness = await AdminHarness.StartAsync(
            cfg => cfg.Admin.GrantedPermissions = new List<string> { "nimbus.command.ping" });

        Assert.True((await harness.RunAsync(new { cmd = "ping" })).GetProperty("ok").GetBoolean());

        var denied = await harness.RunAsync(new { cmd = "bans" });
        Assert.False(denied.GetProperty("ok").GetBoolean());
        Assert.Equal("permission denied", denied.GetProperty("reason").GetString());
        // The refused permission is named so an operator can grant it without guessing.
        Assert.Equal("nimbus.command.bans", denied.GetProperty("permission").GetString());
    }

    [Fact]
    public async Task ASubtreeGrant_CoversItsCommandsAndNothingElse()
    {
        await using var harness = await AdminHarness.StartAsync(
            cfg => cfg.Admin.GrantedPermissions = new List<string> { "nimbus.command.whitelist.*" });
        harness.Registry.Whitelist = new List<Nimbus.Shared.Models.WhitelistEntry>();

        // nimbus.command.whitelist.list sits under the granted subtree.
        Assert.True((await harness.RunAsync(new { cmd = "whitelist-list" })).GetProperty("ok").GetBoolean());

        // nimbus.command.ban does not, and a grant one level down must not reach sideways.
        var denied = await harness.RunAsync(new { cmd = "ban", uid = "uid-1" });
        Assert.Equal("permission denied", denied.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task NoGrantsAtAll_RefusesEverything()
    {
        await using var harness = await AdminHarness.StartAsync(
            cfg => cfg.Admin.GrantedPermissions = new List<string>());

        var denied = await harness.RunAsync(new { cmd = "ping" });

        Assert.False(denied.GetProperty("ok").GetBoolean());
        Assert.Equal("permission denied", denied.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AnUnknownCommand_IsNamedBackToTheCaller()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "self-destruct" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown cmd: self-destruct", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AFrameWithoutACmd_IsRefused()
    {
        await using var harness = await AdminHarness.StartAsync();

        using var conn = await harness.ConnectAsync();
        var reply = await conn.SendRawAsync("""{"uid":"uid-1"}""");

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("missing 'cmd'", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task MalformedJson_IsReportedWithoutLosingTheConnection()
    {
        await using var harness = await AdminHarness.StartAsync();

        using var conn = await harness.ConnectAsync();
        var reply = await conn.SendRawAsync("{ this is not json");

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.True(reply.TryGetProperty("error", out _));
        // A fat-fingered line costs the operator the line, not the session.
        Assert.True((await conn.SendAsync(new { cmd = "ping" })).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task BlankLines_AreSkippedRatherThanAnswered()
    {
        await using var harness = await AdminHarness.StartAsync();

        using var conn = await harness.ConnectAsync();
        // Two empty lines then a real command: the reply that comes back is the ping's, so the
        // blanks produced no output of their own.
        var reply = await conn.SendRawAsync("\n\n" + JsonSerializer.Serialize(new { cmd = "ping" }));

        Assert.True(reply.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task CommandAliases_ReachTheSameHandler()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.Whitelist = new List<Nimbus.Shared.Models.WhitelistEntry>();

        // `whitelist` is the alias operators type; it has to land on whitelist-list rather than
        // on nothing.
        var listed = await harness.RunAsync(new { cmd = "whitelist" });
        Assert.True(listed.GetProperty("ok").GetBoolean());
        Assert.True(listed.TryGetProperty("entries", out _));

        // `drop` is the alias for kick, and reaches far enough to report the missing session.
        var dropped = await harness.RunAsync(new { cmd = "drop", id = 999 });
        Assert.Equal("session not found", dropped.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task CommandNames_AreMatchedWithoutRegardToCase()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "PING" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task TwoOperatorsAtOnce_AreServedOnTheirOwnConnections()
    {
        await using var harness = await AdminHarness.StartAsync(cfg => cfg.Admin.Secret = Secret);

        using var first = await harness.ConnectAsync(Secret);

        // Authentication is per connection, not per listener. A second operator dialling in
        // while the first is authenticated gets no ride on that handshake.
        using var second = await AdminConnection.OpenAsync(harness.Port, CancellationToken.None);
        string? refusal = await second.SendRawForLineAsync("""{"cmd":"ping"}""");
        Assert.Contains("auth required", refusal);
        Assert.True(await second.ClosedByProxyAsync());

        // And the refused connection did not take the first operator's session down with it.
        Assert.True((await first.SendAsync(new { cmd = "ping" })).GetProperty("ok").GetBoolean());

        using var third = await harness.ConnectAsync(Secret);
        Assert.True((await third.SendAsync(new { cmd = "ping" })).GetProperty("ok").GetBoolean());
        Assert.True((await first.SendAsync(new { cmd = "ping" })).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task KickOverTheSocket_DropsTheNamedSessionAndLeavesTheOthers()
    {
        await using var harness = await AdminHarness.StartAsync();
        var target = await harness.JoinAsync("uid-kicked", "leaver");
        var bystander = await harness.JoinAsync("uid-stays", "stayer");

        var reply = await harness.RunAsync(new { cmd = "kick", id = target.Id });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.True(await target.DroppedAsync());
        Assert.False(await bystander.DroppedAsync(500));
    }

    [Fact]
    public async Task ListOverTheSocket_ShowsTheLiveSessionsWithTheirIdentities()
    {
        await using var harness = await AdminHarness.StartAsync();
        await harness.JoinAsync("uid-1", "alice");

        var reply = await harness.RunAsync(new { cmd = "list" });

        var session = reply.GetProperty("sessions").EnumerateArray().Single();
        Assert.Equal("uid-1", session.GetProperty("uid").GetString());
        Assert.Equal("alice", session.GetProperty("player").GetString());
    }
}
