using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Nimbus.Proxy.Tests;

public class UdpSecurityTests
{
    [Fact]
    public async Task TokenAnswerAndClientPlaying_DoNotAuthorizeUdpUntilBackendLoginAcknowledgement()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backend = new TcpListener(IPAddress.Loopback, 0); backend.Start();
        using var front = new TcpListener(IPAddress.Loopback, 0); front.Start();
        var target = new BackendEndpoint { Host = "127.0.0.1", Port = ((IPEndPoint)backend.LocalEndpoint).Port };
        using var player = new TcpClient();
        await player.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)front.LocalEndpoint).Port, stop.Token);
        using var accepted = await front.AcceptTcpClientAsync(stop.Token);
        var table = new UdpAuthorizationTable();
        var cfg = new ProxyConfig(); cfg.Registry.Mode = "disabled";
        var session = new ProxySession(1, cfg, accepted, stop.Token, new SessionServices(UdpAuthorizations: table));
        byte[] query = ClientFrames.LoginTokenQuery();
        Task run = session.RunAsync(new[] { target }, query);
        using var upstream = await backend.AcceptTcpClientAsync(stop.Token);
        try
        {
            await upstream.GetStream().ReadExactlyAsync(new byte[query.Length], stop.Token);
            using var tokenBody = new MemoryStream(); VsWire.WriteStringField(tokenBody, 1, "backend-token");
            byte[] answer = VsWire.BuildServerPacketFrame(77, 77, tokenBody.ToArray());
            await upstream.GetStream().WriteAsync(answer, stop.Token);
            await player.GetStream().ReadExactlyAsync(new byte[answer.Length], stop.Token);
            byte[] playing = ClientFrames.ClientPlaying();
            await player.GetStream().WriteAsync(playing, stop.Token);
            await upstream.GetStream().ReadExactlyAsync(new byte[playing.Length], stop.Token);
            var endpoint = new IPEndPoint(IPAddress.Loopback, 12345);
            Assert.Null(table.Claim(endpoint, ConnectionPacket("backend-token")));
            byte[] ack = VsWire.BuildServerPacketFrame(1, 1, Array.Empty<byte>());
            await upstream.GetStream().WriteAsync(ack, stop.Token);
            await player.GetStream().ReadExactlyAsync(new byte[ack.Length], stop.Token);
            await SessionHarness.WaitForAsync(() => table.Claim(endpoint, ConnectionPacket("backend-token")) != null,
                "the authenticated backend token was not published");
        }
        finally { session.Close(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Null(table.Claim(new IPEndPoint(IPAddress.Loopback, 12345), ConnectionPacket("backend-token")));
    }

    internal static byte[] ConnectionPacket(string token)
    {
        using var connection = new MemoryStream();
        ProtoWire.WriteString(connection, 1, token);
        using var packet = new MemoryStream();
        ProtoWire.WriteTag(packet, 1, 0); ProtoWire.WriteVarint(packet, 1);
        ProtoWire.WriteBytes(packet, 5, connection.ToArray());
        return packet.ToArray();
    }

    [Fact]
    public void AuthenticatedToken_CanClaimOnlyOneEndpoint_AndIsRevokedOnTcpClose()
    {
        var table = new UdpAuthorizationTable();
        var target = new BackendEndpoint();
        var first = new IPEndPoint(IPAddress.Loopback, 12345);
        Assert.Null(table.Claim(first, ConnectionPacket("unknown")));
        table.Publish(1, IPAddress.Loopback, "known", target);
        Assert.Null(table.Claim(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 12345), ConnectionPacket("known")));
        var authorization = Assert.IsType<UdpAuthorizationTable.Authorization>(table.Claim(first, ConnectionPacket("known")));
        Assert.Null(table.Claim(new IPEndPoint(IPAddress.Loopback, 12346), ConnectionPacket("known")));
        Assert.Same(authorization, table.Claim(first, ConnectionPacket("known")));
        table.Remove(1);
        Assert.True(authorization.Revoked.IsCancellationRequested);
        Assert.Null(table.Claim(first, ConnectionPacket("known")));
    }

    [Fact]
    public void DifferentPlayersBehindTheSameAddress_KeepTheirOwnBackend()
    {
        var table = new UdpAuthorizationTable();
        var hub = new BackendEndpoint { ServerId = "hub" };
        var creative = new BackendEndpoint { ServerId = "creative" };
        table.Publish(1, IPAddress.Loopback, "alice", hub);
        table.Publish(2, IPAddress.Loopback, "bob", creative);
        var alice = table.Claim(new IPEndPoint(IPAddress.Loopback, 12345), ConnectionPacket("alice"));
        var bob = table.Claim(new IPEndPoint(IPAddress.Loopback, 12346), ConnectionPacket("bob"));
        Assert.Same(hub, alice!.Target);
        Assert.Same(creative, bob!.Target);
        table.Update(2, hub);
        Assert.Same(hub, bob.Target);
        table.Remove(1);
        Assert.False(bob.Revoked.IsCancellationRequested);
    }

    [Fact]
    public void DuplicateConnectionTokens_AreRejected()
    {
        using var body = new MemoryStream();
        ProtoWire.WriteString(body, 1, "known"); ProtoWire.WriteString(body, 1, "replacement");
        using var packet = new MemoryStream();
        ProtoWire.WriteTag(packet, 1, 0); ProtoWire.WriteVarint(packet, 1);
        ProtoWire.WriteBytes(packet, 5, body.ToArray());
        Assert.False(UdpAuthorizationTable.TryReadToken(packet.ToArray(), out _));
    }

    [Fact]
    public async Task UnauthenticatedSourcePortChurn_CreatesNoUpstreamSockets()
    {
        using var backend = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Dispose();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cfg = new ProxyConfig { Bind = $"127.0.0.1:{port}", Servers = new() { ["hub"] = $"127.0.0.1:{((IPEndPoint)backend.Client.LocalEndPoint!).Port}" } };
        var table = new UdpAuthorizationTable();
        var relay = new UdpRelay(cfg, stop.Token, new UdpRouteOverrides(), table);
        Task loop = relay.RunAsync();
        await relay.Listening.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            for (int i = 0; i < 32; i++)
            {
                using var client = new UdpClient(0);
                byte[] packet = ConnectionPacket("not-authenticated-" + i);
                await client.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, port), stop.Token);
            }
            using var deadline = new CancellationTokenSource(300);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await backend.ReceiveAsync(deadline.Token));
            Assert.Equal(0, relay.ActiveSessionCount);
        }
        finally { stop.Cancel(); await loop; }
    }

    [Fact]
    public async Task TcpRevocation_ClosesTheExistingUdpSocket()
    {
        using var backend = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port; probe.Dispose();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var target = new BackendEndpoint { Host = "127.0.0.1", Port = ((IPEndPoint)backend.Client.LocalEndPoint!).Port };
        var cfg = new ProxyConfig { Bind = $"127.0.0.1:{port}", Servers = new() { ["hub"] = $"127.0.0.1:{target.Port}" } };
        var table = new UdpAuthorizationTable(); table.Publish(1, IPAddress.Loopback, "known", target);
        var relay = new UdpRelay(cfg, stop.Token, new UdpRouteOverrides(), table);
        Task loop = relay.RunAsync();
        await relay.Listening.WaitAsync(TimeSpan.FromSeconds(2));
        using var client = new UdpClient(0); client.Connect(IPAddress.Loopback, port);
        try
        {
            await client.SendAsync(ConnectionPacket("known"), stop.Token);
            await backend.ReceiveAsync(stop.Token);
            Assert.Equal(1, relay.ActiveSessionCount);
            table.Remove(1);
            await SessionHarness.WaitForAsync(() => relay.ActiveSessionCount == 0, "TCP revocation kept its upstream UDP socket alive");
        }
        finally { stop.Cancel(); await loop; }
    }
}
