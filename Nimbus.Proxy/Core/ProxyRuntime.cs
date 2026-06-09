namespace Nimbus.Proxy;

internal sealed class ProxyRuntime : IDisposable
{
    private readonly ProxyListener listener;
    private readonly UdpRelay udp;
    private readonly AdminListener admin;
    private readonly MetricsEndpoint metrics;
    private readonly PluginLoader plugins;

    public ProxyRuntime(ProxyConfig cfg, CancellationToken stopToken, IRegistryClient? registry)
    {
        PersistentDrainStore? drainStore = null;
        if (cfg.Persistence.PersistDrainFlags)
            drainStore = new PersistentDrainStore(ResolveStatePath(cfg.Persistence.DrainFlagsFile));

        listener = new ProxyListener(cfg, stopToken, registry, drainStore);
        udp = new UdpRelay(cfg, stopToken, listener.UdpOverrides);
        plugins = new PluginLoader(new PluginLoaderOptions
        {
            Enabled = cfg.Plugins.Enabled,
            DisabledIds = cfg.Plugins.Disabled,
        });
        admin = new AdminListener(cfg, listener, stopToken, () => plugins.Loaded);
        metrics = new MetricsEndpoint(cfg.Metrics, stopToken);

        var pluginsDir = ResolveStatePath(cfg.Plugins.Directory);
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
