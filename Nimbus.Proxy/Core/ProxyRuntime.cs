namespace Nimbus.Proxy;

internal sealed class ProxyRuntime : IDisposable
{
    private readonly ProxyListener listener;
    private readonly UdpRelay udp;
    private readonly AdminListener admin;
    private readonly MetricsEndpoint metrics;
    private readonly PluginLoader plugins = new();

    public ProxyRuntime(ProxyConfig cfg, CancellationToken stopToken, IRegistryClient? registry)
    {
        PersistentDrainStore? drainStore = null;
        if (cfg.Persistence.PersistDrainFlags)
            drainStore = new PersistentDrainStore(ResolveStatePath(cfg.Persistence.DrainFlagsFile));

        listener = new ProxyListener(cfg, stopToken, registry, drainStore);
        udp = new UdpRelay(cfg, stopToken, listener.UdpOverrides);
        admin = new AdminListener(cfg, listener, stopToken);
        metrics = new MetricsEndpoint(cfg.Metrics, stopToken);

        var pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        try { plugins.LoadAll(pluginsDir, new ProxyApi(listener)); }
        catch (Exception ex) { Log.Warn($"plugins: discovery failed: {ex.Message}"); }
    }

    public Task RunAsync()
        => Task.WhenAll(listener.RunAsync(), udp.RunAsync(), admin.RunAsync(), metrics.RunAsync());

    public void Dispose()
        => plugins.ShutdownAll();

    private static string ResolveStatePath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
}
