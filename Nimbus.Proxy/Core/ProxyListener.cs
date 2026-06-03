using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Nimbus.Proxy;

// Accepts client TCP and tracks live sessions for admin and plugins.
internal sealed class ProxyListener
{
    private readonly ProxyConfig cfg;
    private readonly CancellationToken stopToken;
    private long sessionCounter;
    public ConcurrentDictionary<long, ProxySession> Sessions { get; } = new();

    // Non-null when a registry is configured (mode = "embedded" or "remote").
    public IRegistryClient? Registry { get; }

    public BackendRouter Router { get; }

    // Redirect transfers stage their next reconnect here by UID.
    public StickyRouteTable Stickies { get; } = new();

    // UDP follows TCP swaps through this per-client-IP table.
    public UdpRouteOverrides UdpOverrides { get; } = new();

    public RegistryConfig RegistryCfg => cfg.Registry;
    public ProxyConfig Cfg => cfg;

    // Plugin/event surface. Subscribed handlers run sequentially per-event.
    public EventBus Events { get; } = new();

    public ProxyListener(ProxyConfig cfg, CancellationToken stopToken, IRegistryClient? registry = null,
        PersistentDrainStore? drainStore = null)
    {
        this.cfg = cfg;
        this.stopToken = stopToken;
        Registry = registry;
        Router = new BackendRouter(cfg, registry, drainStore);
    }

    public async Task RunAsync()
    {
        var listenEp = cfg.ListenEndPoint();
        var listener = new TcpListener(listenEp);
        listener.Start();
        var backends = cfg.Backends();
        if (backends.Count > 1)
            Log.Info($"listening on {listenEp} -> pool of {backends.Count} backend(s)");
        else
            Log.Info($"listening on {listenEp} -> backend {backends[0]}");

        _ = Task.Run(() => new StickyRouteSweeper(Stickies, stopToken).RunAsync(), stopToken);
        if (Registry != null)
            _ = Task.Run(() => new TransferIntentDispatcher(cfg, Registry, Sessions, stopToken).RunAsync(), stopToken);
        var sessionRunner = new ClientSessionRunner(Router, Events, stopToken);

        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stopToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                long id = Interlocked.Increment(ref sessionCounter);
                var session = new ProxySession(id, cfg, client, stopToken, Stickies, Registry, UdpOverrides, Events);
                Sessions[id] = session;
                ProxyMetrics.SessionAccepted();
                _ = Task.Run(async () =>
                {
                    try { await sessionRunner.RunAsync(session, client).ConfigureAwait(false); }
                    finally
                    {
                        Sessions.TryRemove(id, out _);
                        ProxyMetrics.SessionClosed();
                    }
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
