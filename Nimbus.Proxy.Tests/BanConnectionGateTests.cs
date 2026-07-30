using System.Net;
using System.Net.Sockets;
using System.Text;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// Drives a real socket session through ClientSessionRunner to pin the ban gate on the path the
/// unit tests could not reach: a client that stays quiet past the first-frame read window, so the
/// pre-connect ban check never runs and the byte pumps are already live when Identification
/// finally arrives.
/// </summary>
public class BanConnectionGateTests
{
    private const string BannedUid = "banned-uid-1";
    private const int FirstFrameTimeoutMs = 200;

    /// <summary>A stand-in backend that records everything it is sent.</summary>
    private sealed class RecordingBackend : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cts = new();
        private readonly List<byte> received = new();
        private readonly object gate = new();

        public RecordingBackend()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public int BytesReceived { get { lock (gate) return received.Count; } }

        public bool Sent(string needle)
        {
            lock (gate) return Encoding.UTF8.GetString(received.ToArray()).Contains(needle);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                    _ = Task.Run(() => ReadLoopAsync(client));
                }
            }
            catch { }
        }

        private async Task ReadLoopAsync(TcpClient client)
        {
            var buf = new byte[4096];
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    int read = await client.GetStream().ReadAsync(buf, cts.Token).ConfigureAwait(false);
                    if (read <= 0) return;
                    lock (gate) received.AddRange(buf.AsSpan(0, read).ToArray());
                }
            }
            catch { }
        }

        public void Dispose()
        {
            cts.Cancel();
            try { listener.Stop(); } catch { }
            cts.Dispose();
        }
    }

    // Identification frame carrying a player uid, shaped the way IdentificationParser reads it:
    // header, then Packet_Client field 2 wrapping the identification body (2 = name, 6 = uid).
    private static byte[] IdentificationFrame(string uid, string name)
    {
        static void Varint(List<byte> to, ulong v)
        {
            while (v >= 0x80) { to.Add((byte)(v | 0x80)); v >>= 7; }
            to.Add((byte)v);
        }
        static void StringField(List<byte> to, int field, string value)
        {
            var utf8 = Encoding.UTF8.GetBytes(value);
            Varint(to, (ulong)((field << 3) | 2));
            Varint(to, (ulong)utf8.Length);
            to.AddRange(utf8);
        }

        var ident = new List<byte>();
        StringField(ident, 2, name);
        StringField(ident, 6, uid);

        var envelope = new List<byte>();
        Varint(envelope, (2 << 3) | 2);
        Varint(envelope, (ulong)ident.Count);
        envelope.AddRange(ident);

        var frame = new byte[4 + envelope.Count];
        int len = envelope.Count;
        frame[0] = (byte)(len >> 24);
        frame[1] = (byte)(len >> 16);
        frame[2] = (byte)(len >> 8);
        frame[3] = (byte)len;
        envelope.CopyTo(frame, 4);
        return frame;
    }

    [Fact]
    public async Task DelayedIdentification_FromABannedPlayer_NeverReachesTheBackend()
    {
        using var backend = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = new ProxyConfig
        {
            Servers = new() { ["default"] = $"127.0.0.1:{backend.Port}" },
            Try = new() { "default" },
        };
        cfg.Registry.Mode = "disabled";
        // Force the pre-connect first-frame read to give up, which is the whole point: the ban
        // check that runs there is skipped and the pumps start unguarded.
        cfg.Status.QueryTimeoutMs = FirstFrameTimeoutMs;

        var registry = new FakeRegistryClient
        {
            Bans = new List<NetworkBan>
            {
                new() { PlayerUid = BannedUid, PlayerName = "griefer", Reason = "griefing", ExpiresAtUnix = 0 },
            },
        };
        var bans = new BanCache(registry, cts.Token);
        await bans.RefreshAsync();
        Assert.Equal(1, bans.Count);

        // Front door: accept one connection and hand the server side to a session.
        var front = new TcpListener(IPAddress.Loopback, 0);
        front.Start();
        int frontPort = ((IPEndPoint)front.LocalEndpoint).Port;

        var events = new EventBus();
        var stickies = new StickyRouteTable();
        var router = new BackendRouter(cfg, registry: null);
        var runner = new ClientSessionRunner(router, events, new ServerStatusResponder(cfg, registry: null, () => 0, cts.Token), stickies, cfg, cts.Token);

        var accepted = front.AcceptTcpClientAsync(cts.Token).AsTask();
        using var player = new TcpClient();
        await player.ConnectAsync(IPAddress.Loopback, frontPort, cts.Token);
        var serverSide = await accepted;

        var session = new ProxySession(1, cfg, serverSide, cts.Token, stickies, registry: null,
            udpOverrides: null, events: events, bans: bans);
        var running = runner.RunAsync(session, serverSide);

        // Stay silent past the first-frame window, then identify as the banned player.
        await Task.Delay(FirstFrameTimeoutMs * 3, cts.Token);
        await player.GetStream().WriteAsync(IdentificationFrame(BannedUid, "griefer"), cts.Token);
        await player.GetStream().FlushAsync(cts.Token);

        // Give the pump time to forward, if it were going to.
        await Task.Delay(1000, cts.Token);

        Assert.False(backend.Sent(BannedUid), "the banned player's Identification reached the backend");
        Assert.Equal(0, backend.BytesReceived);

        session.Close();
        try { await running.WaitAsync(TimeSpan.FromSeconds(5), cts.Token); } catch { }
        front.Stop();
    }
}
