namespace Nimbus.Proxy;

internal sealed class ProxyRuntime : IDisposable
{
    private readonly ProxyConfig cfg;
    private readonly ProxyListener listener;
    private readonly UdpRelay udp;
    private readonly AdminListener admin;
    private readonly MetricsEndpoint metrics;
    private readonly PluginLoader plugins;
    private readonly string pluginsDir;
    private readonly Func<ProxyConfig> configLoader;

    public ProxyRuntime(ProxyConfig cfg, CancellationToken stopToken, IRegistryClient? registry,
        Func<ProxyConfig> configLoader)
    {
        this.cfg = cfg;
        this.configLoader = configLoader;

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
        pluginsDir = ResolveStatePath(cfg.Plugins.Directory);
        admin = new AdminListener(cfg, listener, stopToken, () => plugins.Loaded, Reload);
        long startUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        metrics = new MetricsEndpoint(cfg.Metrics, stopToken,
            ct => StatusReport.BuildAsync(cfg, listener.Router, registry, startUnix, ct));

        try { plugins.LoadAll(pluginsDir, new ProxyApi(listener)); }
        catch (Exception ex) { Log.Warn($"plugins: discovery failed: {ex.Message}"); }

        if (cfg.Servers.Count > 0)
            Log.Info($"backends: {string.Join(", ", cfg.Servers.Keys)}");
        if (plugins.Loaded.Count > 0)
            Log.Info($"plugins: {string.Join(", ", plugins.Loaded.Select(p => p.Instance.Name))}");
    }

    public string Reload()
    {
        ProxyConfig fresh;
        try { fresh = configLoader(); }
        catch (Exception ex) { return $"reload failed: config error: {ex.Message}"; }

        try
        {
            var validation = ProxyConfigValidator.Validate(fresh);
            if (!validation.IsValid)
                return "reload failed: " + string.Join("; ", validation.Errors);
            foreach (var w in validation.Warnings)
                Log.Warn("config warning: " + w);
        }
        catch (Exception ex) { return $"reload failed: validation error: {ex.Message}"; }

        cfg.UpdateFrom(fresh);
        Log.Configure(cfg.Logging.Verbose);

        try { plugins.Reload(pluginsDir, listener.Events, new ProxyApi(listener), cfg.Plugins.Disabled); }
        catch (Exception ex) { Log.Warn($"plugins: reload failed: {ex.Message}"); }

        return $"{cfg.Servers.Count} server(s), {plugins.Loaded.Count} plugin(s)";
    }

    public Task RunAsync()
        => Task.WhenAll(listener.RunAsync(), udp.RunAsync(), admin.RunAsync(), metrics.RunAsync());

    public void Dispose()
        => plugins.ShutdownAll();

    private static string ResolveStatePath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
}
