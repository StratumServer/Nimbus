using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The UDP relay, over real loopback sockets. Vintage Story carries player positions over UDP
/// and ties the flow to the source endpoint that first sent its LoginToken, so a relay that
/// mixes two clients' datagrams together, or that changes the source endpoint a backend sees
/// mid-session, does not fail loudly. It desynchronises positions, which is the kind of bug
/// that gets reported as "the server feels laggy".
///
/// Nothing here is stubbed. A test binds a socket to stand in for the backend, sends real
/// datagrams from real client sockets, and reads what arrived and where from.
/// </summary>
public class UdpRelayTests
{
    /// <summary>A relay bound to a loopback port with a fake backend behind it, plus whatever
    /// extra backends a test asked for so it can watch traffic move between them.</summary>
    private sealed class Relay : IAsyncDisposable
    {
        public required int Port { get; init; }
        public required Backend Default { get; init; }
        public required UdpRouteOverrides Overrides { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task Loop { get; init; }

        public static async Task<Relay> StartAsync()
        {
            var backend = new Backend();
            int port = FreeUdpPort();
            var cfg = new ProxyConfig
            {
                Bind = $"127.0.0.1:{port}",
                Servers = new Dictionary<string, string> { ["hub"] = $"127.0.0.1:{backend.Port}" },
                Try = new List<string> { "hub" },
            };
            var cts = new CancellationTokenSource();
            var overrides = new UdpRouteOverrides();
            var relay = new UdpRelay(cfg, cts.Token, overrides);
            var r = new Relay
            {
                Port = port,
                Default = backend,
                Overrides = overrides,
                Cts = cts,
                Loop = Task.Run(relay.RunAsync),
            };
            await r.WaitUntilListeningAsync();
            return r;
        }

        /// <summary>The relay binds its socket inside RunAsync, so a datagram sent before that
        /// lands nowhere. Poll until one gets through rather than sleeping a guessed interval.</summary>
        private async Task WaitUntilListeningAsync()
        {
            using var probe = new UdpClient(0);
            for (int attempt = 0; attempt < 200; attempt++)
            {
                await probe.SendAsync(Encoding.ASCII.GetBytes("probe"), 5,
                    new IPEndPoint(IPAddress.Loopback, Port));
                if (await Default.TryReceiveAsync(50) != null) return;
            }
            Assert.Fail($"the udp relay never came up on 127.0.0.1:{Port}");
        }

        public UdpClient NewClient()
        {
            var c = new UdpClient(0);
            c.Connect(IPAddress.Loopback, Port);
            return c;
        }

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            try { await Loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* shutting down */ }
            Cts.Dispose();
            Default.Dispose();
        }
    }

    /// <summary>A socket standing in for a backend's UDP port. Records what arrived and the
    /// endpoint it arrived from, which is the thing Vintage Story keys its flows on.</summary>
    private sealed class Backend : IDisposable
    {
        private readonly UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));

        public int Port => ((IPEndPoint)socket.Client.LocalEndPoint!).Port;

        public async Task<UdpReceiveResult?> TryReceiveAsync(int millis = 3000)
        {
            using var cts = new CancellationTokenSource(millis);
            try { return await socket.ReceiveAsync(cts.Token); }
            catch (OperationCanceledException) { return null; }
            catch (ObjectDisposedException) { return null; }
        }

        /// <summary>Waits for a datagram whose payload is not the startup probe.</summary>
        public async Task<UdpReceiveResult> ReceiveRealAsync(int millis = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(millis);
            while (DateTime.UtcNow < deadline)
            {
                var res = await TryReceiveAsync(500);
                if (res == null) continue;
                if (Encoding.ASCII.GetString(res.Value.Buffer) == "probe") continue;
                return res.Value;
            }
            Assert.Fail("no datagram reached the backend");
            return default;
        }

        public Task ReplyAsync(IPEndPoint to, string payload)
            => socket.SendAsync(Encoding.ASCII.GetBytes(payload), payload.Length, to);

        public void Dispose() => socket.Dispose();
    }

    private static async Task<string?> ReadAsync(UdpClient client, int millis = 3000)
    {
        using var cts = new CancellationTokenSource(millis);
        try { return Encoding.ASCII.GetString((await client.ReceiveAsync(cts.Token)).Buffer); }
        catch (OperationCanceledException) { return null; }
    }

    private static Task SendAsync(UdpClient client, string payload)
        => client.SendAsync(Encoding.ASCII.GetBytes(payload), payload.Length);

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    // ---- carrying datagrams both ways ----

    [Fact]
    public async Task ADatagramFromAClient_ReachesTheBackendUnchanged()
    {
        await using var relay = await Relay.StartAsync();
        using var client = relay.NewClient();

        await SendAsync(client, "position-1");

        var arrived = await relay.Default.ReceiveRealAsync();
        // Byte for byte: the relay is a pipe, and a payload it reframed would be a position
        // packet the backend cannot parse.
        Assert.Equal("position-1", Encoding.ASCII.GetString(arrived.Buffer));
    }

    [Fact]
    public async Task AReplyFromTheBackend_ReachesTheClientThatStartedTheFlow()
    {
        await using var relay = await Relay.StartAsync();
        using var client = relay.NewClient();
        await SendAsync(client, "position-1");
        var arrived = await relay.Default.ReceiveRealAsync();

        await relay.Default.ReplyAsync(arrived.RemoteEndPoint, "world-state");

        // The return path is the half a naive relay forgets: the backend answers the relay's
        // upstream socket, and only the relay knows which client that stands for.
        Assert.Equal("world-state", await ReadAsync(client));
    }

    [Fact]
    public async Task EveryDatagramFromOneClient_ArrivesFromTheSameSourceTheBackendFirstSaw()
    {
        await using var relay = await Relay.StartAsync();
        using var client = relay.NewClient();

        await SendAsync(client, "position-1");
        var first = await relay.Default.ReceiveRealAsync();
        await SendAsync(client, "position-2");
        var second = await relay.Default.ReceiveRealAsync();

        // Vintage Story ties the UDP flow to the endpoint that first sent the LoginToken. A
        // relay opening a fresh socket per datagram would have the backend drop everything after
        // the first one.
        Assert.Equal("position-2", Encoding.ASCII.GetString(second.Buffer));
        Assert.Equal(first.RemoteEndPoint, second.RemoteEndPoint);
    }

    // ---- keeping clients apart ----

    [Fact]
    public async Task TwoClients_GetTheirOwnUpstreamSocketsAndTheirOwnReplies()
    {
        await using var relay = await Relay.StartAsync();
        using var alice = relay.NewClient();
        using var bob = relay.NewClient();

        await SendAsync(alice, "from-alice");
        var fromAlice = await relay.Default.ReceiveRealAsync();
        await SendAsync(bob, "from-bob");
        var fromBob = await relay.Default.ReceiveRealAsync();

        // One socket for both would leave the backend unable to tell two players apart, and the
        // replies would go to whichever of them sent last.
        Assert.NotEqual(fromAlice.RemoteEndPoint, fromBob.RemoteEndPoint);
        Assert.Equal("from-alice", Encoding.ASCII.GetString(fromAlice.Buffer));
        Assert.Equal("from-bob", Encoding.ASCII.GetString(fromBob.Buffer));

        await relay.Default.ReplyAsync(fromAlice.RemoteEndPoint, "for-alice");
        await relay.Default.ReplyAsync(fromBob.RemoteEndPoint, "for-bob");

        Assert.Equal("for-alice", await ReadAsync(alice));
        Assert.Equal("for-bob", await ReadAsync(bob));
    }

    // ---- following a player across a swap ----

    [Fact]
    public async Task AnOverrideSetBeforeTheFirstDatagram_SendsItToThatBackendInstead()
    {
        await using var relay = await Relay.StartAsync();
        using var creative = new Backend();
        relay.Overrides.Set(IPAddress.Loopback,
            new BackendEndpoint { Host = "127.0.0.1", Port = creative.Port, ServerId = "creative" });
        using var client = relay.NewClient();

        await SendAsync(client, "position-1");

        // A player who joined straight onto a non-default backend has an override in place
        // before their first packet, and the default backend must not see it.
        Assert.Equal("position-1", Encoding.ASCII.GetString((await creative.ReceiveRealAsync()).Buffer));
        Assert.Null(await relay.Default.TryReceiveAsync(300));
    }

    [Fact]
    public async Task AnOverrideSetMidFlow_MovesTheClientsDatagramsToTheNewBackend()
    {
        await using var relay = await Relay.StartAsync();
        using var creative = new Backend();
        using var client = relay.NewClient();

        await SendAsync(client, "before-swap");
        Assert.Equal("before-swap", Encoding.ASCII.GetString((await relay.Default.ReceiveRealAsync()).Buffer));

        // What a TCP swap does: the player is now on creative, and their positions have to
        // follow. Leaving UDP pointed at the old backend is how a swapped player ends up
        // standing still on everybody else's screen.
        relay.Overrides.Set(IPAddress.Loopback,
            new BackendEndpoint { Host = "127.0.0.1", Port = creative.Port, ServerId = "creative" });
        await SendAsync(client, "after-swap");

        Assert.Equal("after-swap", Encoding.ASCII.GetString((await creative.ReceiveRealAsync()).Buffer));
        Assert.Null(await relay.Default.TryReceiveAsync(300));
    }

    [Fact]
    public async Task AClientMovedToANewBackend_GetsItsRepliesFromThere()
    {
        await using var relay = await Relay.StartAsync();
        using var creative = new Backend();
        using var client = relay.NewClient();
        await SendAsync(client, "before-swap");
        await relay.Default.ReceiveRealAsync();

        relay.Overrides.Set(IPAddress.Loopback,
            new BackendEndpoint { Host = "127.0.0.1", Port = creative.Port, ServerId = "creative" });
        await SendAsync(client, "after-swap");
        var arrived = await creative.ReceiveRealAsync();
        await creative.ReplyAsync(arrived.RemoteEndPoint, "creative-world-state");

        // The retarget tears down the old upstream socket and its reply pump, so this passing
        // means a fresh pump was started rather than the client being left deaf.
        Assert.Equal("creative-world-state", await ReadAsync(client));
    }

    [Fact]
    public async Task AnOverridePointingWhereItAlreadyPoints_DoesNotChurnTheSession()
    {
        await using var relay = await Relay.StartAsync();
        using var client = relay.NewClient();
        await SendAsync(client, "position-1");
        var first = await relay.Default.ReceiveRealAsync();

        relay.Overrides.Set(IPAddress.Loopback,
            new BackendEndpoint { Host = "127.0.0.1", Port = relay.Default.Port, ServerId = "hub" });
        await SendAsync(client, "position-2");
        var second = await relay.Default.ReceiveRealAsync();

        // Same host and port, so there is nothing to rebind. Tearing the socket down anyway
        // would hand the backend a new source endpoint and break the flow it keyed on.
        Assert.Equal(first.RemoteEndPoint, second.RemoteEndPoint);
    }

    [Fact]
    public async Task AnOverrideCleared_SendsTheClientBackToTheDefaultBackend()
    {
        await using var relay = await Relay.StartAsync();
        using var creative = new Backend();
        relay.Overrides.Set(IPAddress.Loopback,
            new BackendEndpoint { Host = "127.0.0.1", Port = creative.Port, ServerId = "creative" });
        using var client = relay.NewClient();
        await SendAsync(client, "on-creative");
        await creative.ReceiveRealAsync();

        relay.Overrides.Clear(IPAddress.Loopback);
        await SendAsync(client, "back-on-hub");

        // The TCP layer clears the override when the session closes, and a stale one would keep
        // sending the next player on that IP to a backend they never joined.
        Assert.Equal("back-on-hub", Encoding.ASCII.GetString((await relay.Default.ReceiveRealAsync()).Buffer));
    }

    // ---- a port it cannot have ----

    [Fact]
    public async Task ARelayThatCannotBindItsPort_StandsDownInsteadOfTakingTheProxyWithIt()
    {
        int port = FreeUdpPort();
        using var squatter = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        var cfg = new ProxyConfig
        {
            Bind = $"127.0.0.1:{port}",
            Servers = new Dictionary<string, string> { ["hub"] = "127.0.0.1:42421" },
            Try = new List<string> { "hub" },
        };
        using var cts = new CancellationTokenSource();

        var loop = new UdpRelay(cfg, cts.Token, new UdpRouteOverrides()).RunAsync();

        // UDP is an optimisation: clients fall back to carrying positions over TCP without it.
        // Throwing out of RunAsync here would take down a proxy that could still serve players.
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(loop.IsCompletedSuccessfully);
    }
}
