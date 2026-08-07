using System.Net;
using System.Net.Sockets;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The answer a Vintage Story client's server list entry is built from. It is the first thing a
/// player sees of a network and the only thing they see of it when they are not connected, so a
/// ping that goes unanswered or comes back with the wrong numbers is a network that looks down
/// or looks full.
///
/// Driven over a real socket, because closing the connection after the answer is part of the
/// contract: the query is one request per connection and a pinger left hanging counts it as a
/// timeout.
/// </summary>
public class ServerStatusResponderTests
{
    private static ProxyConfig Config(Action<StatusConfig>? configure = null)
    {
        var cfg = new ProxyConfig();
        cfg.Registry.Mode = "disabled";
        cfg.Status.Name = "Stratum Network";
        cfg.Status.Motd = "four worlds, one login";
        cfg.Status.MaxPlayers = 100;
        cfg.Status.GameMode = "survival";
        cfg.Status.ServerVersion = "";
        configure?.Invoke(cfg.Status);
        return cfg;
    }

    /// <summary>The bare query frame a client's server list sends, built with the independent
    /// wire writer.</summary>
    private static byte[] QueryFrame() => ProtoWire.Frame(new byte[] { 8, 15 });

    private sealed record Answer(string Name, string Motd, int Players, int MaxPlayers, string GameMode,
        bool Password, string Version);

    private static Answer? ParseAnswer(byte[] bytes)
    {
        if (bytes.Length < 4) return null;
        var (_, _, payload) = ProtoWire.ParseFrame(bytes);
        var envelope = ProtoWire.ReadFields(payload);
        if (!envelope.Any(f => f.Number == 90 && f.Varint == 28)) return null;
        var body = ProtoWire.ReadFields(envelope.Single(f => f.Number == 28).Bytes);

        string Str(int n) => body.FirstOrDefault(f => f.Number == n) is { } f ? ProtoWire.Utf8(f) : "";
        int Num(int n) => body.FirstOrDefault(f => f.Number == n) is { } f ? (int)f.Varint : 0;

        return new Answer(Str(1), Str(2), Num(3), Num(4), Str(5), Num(6) != 0, Str(7));
    }

    /// <summary>Sends a first frame at a responder and returns what came back, or null when the
    /// responder declined to handle it.</summary>
    private static async Task<(bool Handled, Answer? Answer, bool Closed)> AskAsync(
        ProxyConfig cfg, byte[] firstFrame, IRegistryClient? registry = null, int sessions = 0)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var front = new TcpListener(IPAddress.Loopback, 0);
        front.Start();
        try
        {
            int port = ((IPEndPoint)front.LocalEndpoint).Port;
            var accepted = front.AcceptTcpClientAsync(cts.Token).AsTask();
            using var pinger = new TcpClient();
            await pinger.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            using var serverSide = await accepted;

            var responder = new ServerStatusResponder(cfg, registry, () => sessions, cts.Token);
            bool handled = await responder.TryHandleAsync(serverSide, firstFrame);

            var sink = new MemoryStream();
            var buf = new byte[4096];
            bool closed = false;
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            readCts.CancelAfter(1500);
            try
            {
                while (true)
                {
                    int read = await pinger.GetStream().ReadAsync(buf, readCts.Token);
                    if (read <= 0) { closed = true; break; }
                    sink.Write(buf, 0, read);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { closed = true; }

            return (handled, ParseAnswer(sink.ToArray()), closed);
        }
        finally { front.Stop(); }
    }

    [Fact]
    public async Task AQueryPing_IsAnsweredWithTheConfiguredIdentityAndTheConnectionClosed()
    {
        var (handled, answer, closed) = await AskAsync(Config(), QueryFrame(), sessions: 3);

        Assert.True(handled);
        Assert.NotNull(answer);
        Assert.Equal("Stratum Network", answer!.Name);
        Assert.Equal("four worlds, one login", answer.Motd);
        Assert.Equal("survival", answer.GameMode);
        Assert.Equal(100, answer.MaxPlayers);
        // Without a registry the proxy counts what it is holding itself.
        Assert.Equal(3, answer.Players);
        // One request per connection: a pinger left hanging records a timeout, which shows in the
        // list as an offline server.
        Assert.True(closed);
    }

    [Fact]
    public async Task AFirstFrameThatIsNotAQuery_IsLeftForTheSessionToHandle()
    {
        var (handled, answer, _) = await AskAsync(Config(), ClientFrames.LoginTokenQuery());

        // Answering a real join with a status packet would leave the player looking at a list
        // entry instead of a world.
        Assert.False(handled);
        Assert.Null(answer);
    }

    [Fact]
    public async Task WithTheStatusResponderTurnedOff_AQueryIsLeftAlone()
    {
        var (handled, _, _) = await AskAsync(Config(s => s.Enabled = false), QueryFrame());

        // Operators who front the proxy with their own list entry turn this off; it must then
        // not answer at all rather than answer differently.
        Assert.False(handled);
    }

    [Fact]
    public async Task APasswordProtectedNetwork_SaysSoInTheListing()
    {
        var (_, answer, _) = await AskAsync(Config(s => s.Password = true), QueryFrame());

        // The client greys out the join button on this, so getting it wrong sends players at a
        // door they cannot open.
        Assert.True(answer!.Password);
    }

    [Fact]
    public async Task TheConfiguredVersion_IsWhatIsAdvertisedWhenTheOperatorSetOne()
    {
        var (_, answer, _) = await AskAsync(Config(s => s.ServerVersion = "1.22.6"), QueryFrame());

        Assert.Equal("1.22.6", answer!.Version);
    }

    [Fact]
    public async Task WithARegistry_ThePlayerCountAndCapacityComeFromTheWholeNetwork()
    {
        var registry = new FakeRegistryClient
        {
            Snapshot = new NetworkSnapshot
            {
                TotalPlayers = 41,
                TotalCapacity = 80,
                Backends = { new BackendSnapshot { ServerId = "hub", GameVersion = "1.22.6" } },
            },
        };

        var (_, answer, _) = await AskAsync(Config(), QueryFrame(), registry, sessions: 2);

        // The proxy holds two sockets but the network has 41 players on it. Advertising the
        // proxy's own count would show a busy network as empty.
        Assert.Equal(41, answer!.Players);
        Assert.Equal(80, answer.MaxPlayers);
        // And the version is read off a live backend rather than left for the operator to keep
        // in step by hand through every game update.
        Assert.Equal("1.22.6", answer.Version);
    }

    [Fact]
    public async Task AConfiguredVersion_WinsOverWhatTheBackendsReport()
    {
        var registry = new FakeRegistryClient
        {
            Snapshot = new NetworkSnapshot
            {
                Backends = { new BackendSnapshot { ServerId = "hub", GameVersion = "1.22.6" } },
            },
        };

        var (_, answer, _) = await AskAsync(Config(s => s.ServerVersion = "1.21.0"), QueryFrame(), registry);

        Assert.Equal("1.21.0", answer!.Version);
    }

    [Fact]
    public async Task ARegistryReportingNoCapacity_LeavesTheConfiguredMaximumAlone()
    {
        var registry = new FakeRegistryClient
        {
            Snapshot = new NetworkSnapshot { TotalPlayers = 0, TotalCapacity = 0 },
        };

        var (_, answer, _) = await AskAsync(Config(s => s.MaxPlayers = 100), QueryFrame(), registry);

        // A network whose backends have not reported capacity yet would otherwise advertise zero
        // slots, which reads as full.
        Assert.Equal(100, answer!.MaxPlayers);
    }

    [Fact]
    public async Task ARegistryThatIsDown_FallsBackToWhatTheProxyKnowsItself()
    {
        var registry = new FakeRegistryClient { Throw = true };

        var (handled, answer, _) = await AskAsync(Config(), QueryFrame(), registry, sessions: 5);

        // A registry outage must not take the list entry down with it: the network is still
        // reachable and still worth showing.
        Assert.True(handled);
        Assert.Equal(5, answer!.Players);
        Assert.Equal(100, answer.MaxPlayers);
    }

    [Fact]
    public async Task ANetworkWithNobodyOnIt_OmitsTheZeroRatherThanSendingIt()
    {
        var (_, answer, _) = await AskAsync(Config(), QueryFrame(), sessions: 0);

        // proto3 presence: the builder leaves zero fields out entirely, which is what a vanilla
        // server sends and what the client's parser expects to be missing.
        Assert.Equal(0, answer!.Players);
    }
}
