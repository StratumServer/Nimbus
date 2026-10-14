using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Nimbus.Proxy.Tests;

public class ProxyListenerAdmissionTests
{
    [Fact]
    public async Task BackendLoginAcknowledgement_ReleasesCapacityForTheNextHandshake()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backend = new TcpListener(IPAddress.Loopback, 0); backend.Start();
        using var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
        int port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        var cfg = new ProxyConfig { Bind = $"127.0.0.1:{port}", Servers = new() { ["hub"] = $"127.0.0.1:{((IPEndPoint)backend.LocalEndpoint).Port}" } };
        cfg.Registry.Mode = "disabled";
        cfg.Advanced.MaxPendingConnectionsPerAddress = 1;
        var listener = new ProxyListener(cfg, stop.Token);
        Task loop = listener.RunAsync();
        using var player = new TcpClient();
        using var next = new TcpClient();
        try
        {
            await player.ConnectAsync(IPAddress.Loopback, port, stop.Token);
            byte[] query = ClientFrames.LoginTokenQuery();
            await player.GetStream().WriteAsync(query, stop.Token);
            using var upstream = await backend.AcceptTcpClientAsync(stop.Token);
            await upstream.GetStream().ReadExactlyAsync(new byte[query.Length], stop.Token);
            Assert.Equal(1, listener.PendingCount);
            byte[] ack = VsWire.BuildServerPacketFrame(1, 1, Array.Empty<byte>());
            await upstream.GetStream().WriteAsync(ack, stop.Token);
            await SessionHarness.WaitForAsync(() => listener.PendingCount == 0, "backend login retained pre-authentication capacity");
            await next.ConnectAsync(IPAddress.Loopback, port, stop.Token);
            await SessionHarness.WaitForAsync(() => listener.Sessions.Count == 2, "the next handshake was not admitted");
            Assert.Equal(1, listener.PendingCount);
        }
        finally { stop.Cancel(); await loop; }
    }

    [Fact]
    public async Task ClientPlayingCannotReleaseThePreAuthenticationSlot()
    {
        using var harness = await SessionHarness.StartAsync();
        await harness.ReachReadyAsync();
        Assert.False(harness.Session.AuthenticationCompleted.IsCompleted);
    }

    [Fact]
    public async Task HandshakeDeadlineClosesAClientThatNeverSendsAFrame()
    {
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        int port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backend = new RecordingBackend();
        var cfg = new ProxyConfig { Bind = $"127.0.0.1:{port}", Servers = new() { ["hub"] = $"127.0.0.1:{backend.Port}" } };
        cfg.Registry.Mode = "disabled";
        cfg.Status.QueryTimeoutMs = 10000;
        cfg.Advanced.HandshakeTimeoutSeconds = 1;
        var listener = new ProxyListener(cfg, stop.Token);
        Task loop = listener.RunAsync();
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, stop.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            int read = await client.GetStream().ReadAsync(new byte[1], deadline.Token);
            Assert.Equal(0, read);
            await SessionHarness.WaitForAsync(() => listener.Sessions.Count == 0, "expired handshake retained its admission slot");
            Assert.Equal(0, backend.Connections);
        }
        finally { stop.Cancel(); await loop; }
    }

    [Fact]
    public async Task IncompleteConnectionsCannotExceedThePerAddressCapacity()
    {
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var backend = new RecordingBackend();
        var cfg = new ProxyConfig { Bind = $"127.0.0.1:{port}", Servers = new() { ["hub"] = $"127.0.0.1:{backend.Port}" } };
        cfg.Registry.Mode = "disabled";
        cfg.Status.QueryTimeoutMs = 10000;
        cfg.Advanced.MaxPendingConnectionsPerAddress = 2;
        var listener = new ProxyListener(cfg, stop.Token);
        Task loop = listener.RunAsync();
        using var first = new TcpClient();
        using var second = new TcpClient();
        using var rejected = new TcpClient();
        try
        {
            await first.ConnectAsync(IPAddress.Loopback, port, stop.Token);
            await second.ConnectAsync(IPAddress.Loopback, port, stop.Token);
            await SessionHarness.WaitForAsync(() => listener.Sessions.Count == 2, "two pending handshakes were not admitted");
            await rejected.ConnectAsync(IPAddress.Loopback, port, stop.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            byte[] buffer = new byte[1];
            int read;
            try { read = await rejected.GetStream().ReadAsync(buffer, deadline.Token); }
            catch (IOException) { read = 0; }
            Assert.Equal(0, read);
            Assert.Equal(2, listener.Sessions.Count);
            Assert.Equal(0, backend.Connections);
        }
        finally { stop.Cancel(); await loop; }
    }
}
