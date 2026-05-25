using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Nimbus.Proxy;

// Accepts client TCP connections, hands each to a ProxySession, and tracks live sessions so
// the admin endpoint can look them up by id.
internal sealed class ProxyListener
{
    private readonly ProxyConfig cfg;
    private readonly CancellationToken stopToken;
    private long sessionCounter;
    public ConcurrentDictionary<long, ProxySession> Sessions { get; } = new();

    // Non-null when Nimbus.Enabled is true.
    public RegistryClient? Registry { get; }

    // "Next reconnect for this uid goes here" routes. Populated by the disconnect-transfer path
    // and consumed by ProxySession on Identification capture.
    public StickyRouteTable Stickies { get; } = new();

    // Per-client-IP UDP routing overrides. Set when a TCP swap retargets a session, so the UDP
    // relay also retargets. Cleared on session close.
    public UdpRouteOverrides UdpOverrides { get; } = new();

    public NimbusConfig NimbusCfg => cfg.Nimbus;

    public ProxyListener(ProxyConfig cfg, CancellationToken stopToken, RegistryClient? registry = null)
    {
        this.cfg = cfg;
        this.stopToken = stopToken;
        Registry = registry;
    }

    public async Task RunAsync()
    {
        var bindAddr = IPAddress.Parse(cfg.ListenHost == "0.0.0.0" ? "0.0.0.0" : cfg.ListenHost);
        var listener = new TcpListener(bindAddr, cfg.ListenPort);
        listener.Start();
        Log.Info($"listening on {bindAddr}:{cfg.ListenPort} -> backend {cfg.DefaultBackend}");

        // Periodic sweep of expired sticky routes. Without this, staged routes for players who
        // never reconnect would accumulate.
        var sweepTask = Task.Run(async () =>
        {
            while (!stopToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(60), stopToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                int dropped = Stickies.SweepExpired();
                if (dropped > 0) Log.Trace($"sticky sweep: dropped {dropped} expired entries");
            }
        }, stopToken);

        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stopToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                long id = Interlocked.Increment(ref sessionCounter);
                var session = new ProxySession(id, cfg, client, stopToken, Stickies, Registry, UdpOverrides);
                Sessions[id] = session;
                _ = Task.Run(async () =>
                {
                    try { await session.RunAsync(cfg.DefaultBackend).ConfigureAwait(false); }
                    catch (Exception ex) { Log.Warn($"[s{id}] session crashed: {ex.GetType().Name}: {ex.Message}"); }
                    finally { Sessions.TryRemove(id, out _); }
                }, stopToken);
            }
        }
        finally
        {
            listener.Stop();
            Log.Info("listener stopped");
        }
    }
}
