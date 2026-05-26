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

    public BackendRouter Router { get; }

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
        Router = new BackendRouter(cfg, registry);
    }

    public async Task RunAsync()
    {
        var bindAddr = IPAddress.Parse(cfg.ListenHost == "0.0.0.0" ? "0.0.0.0" : cfg.ListenHost);
        var listener = new TcpListener(bindAddr, cfg.ListenPort);
        listener.Start();
        if (cfg.Backends.Count > 0)
            Log.Info($"listening on {bindAddr}:{cfg.ListenPort} -> pool of {cfg.Backends.Count} backend(s)");
        else
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
                    try
                    {
                        BackendEndpoint? target;
                        string? selectReason;
                        using (var selectCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken))
                        {
                            selectCts.CancelAfter(TimeSpan.FromSeconds(5));
                            (target, selectReason) = await Router.SelectAsync(selectCts.Token).ConfigureAwait(false);
                        }
                        if (target == null)
                        {
                            Log.Warn($"[s{id}] no healthy backend: {selectReason}; sending forged disconnect");
                            await TryForgeDisconnectAsync(client, $"No backend available right now ({selectReason}). Please try again shortly.").ConfigureAwait(false);
                            try { client.Close(); } catch { }
                            return;
                        }
                        await session.RunAsync(target).ConfigureAwait(false);
                    }
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

    // Best-effort: write a forged Packet_ServerDisconnect to a client we never wired upstream.
    // Swallow errors; the only purpose is to give the player a useful message before close.
    private static async Task TryForgeDisconnectAsync(TcpClient client, string message)
    {
        try
        {
            var frame = DisconnectBuilder.BuildDisconnectFrame(message);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.GetStream().WriteAsync(frame, cts.Token).ConfigureAwait(false);
            await client.GetStream().FlushAsync(cts.Token).ConfigureAwait(false);
            try { await Task.Delay(150, cts.Token).ConfigureAwait(false); } catch { }
        }
        catch { }
    }
}
