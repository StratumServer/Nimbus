using Nimbus.Shared;

namespace Nimbus.Proxy;

internal static class Program
{
    private const string ConfigFileName = "nimbus.proxy.toml";

    private static async Task<int> Main(string[] args)
    {
        Log.Info($"Nimbus {NimbusProtocol.NimbusVersion} starting");
        UpdateChecker.StartBackgroundCheck();

        ProxyConfig cfg;
        try { cfg = LoadConfig(); }
        catch (Exception ex) { Log.Error("config load failed: " + ex.Message); return 2; }

        Log.Configure(cfg.Logging.Verbose);
        try
        {
            var validation = ProxyConfigValidator.Validate(cfg);
            foreach (var warning in validation.Warnings)
                Log.Warn("config warning: " + warning);
            if (!validation.IsValid)
            {
                foreach (var error in validation.Errors)
                    Log.Error("config error: " + error);
                return 2;
            }
        }
        catch (Exception ex) { Log.Error("config invalid: " + ex.Message); return 2; }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Log.Info("ctrl+c received, shutting down"); cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        ProxyRegistryHost registryHost;
        try
        {
            registryHost = ProxyRegistryHost.Build(cfg, cts.Token);
        }
        catch (Exception ex) { Log.Error("registry init failed: " + ex.Message); return 2; }

        await using var registryDispose = registryHost;
        using var runtime = new ProxyRuntime(cfg, cts.Token, registryHost.Client, LoadConfig);

        try
        {
            await runtime.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("fatal: " + ex);
            return 1;
        }
        return 0;
    }

    internal static ProxyConfig LoadConfig()
    {
        var path = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        var jsonSibling = Path.ChangeExtension(path, ".json");
        // The legacy nimbus.proxy.json shape (pre-Velocity layout) doesn't map onto the new
        // schema. Move it aside so LoadOrCreate writes a fresh default TOML rather than
        // picking up incompatible fields.
        if (!File.Exists(path) && File.Exists(jsonSibling))
        {
            try { File.Move(jsonSibling, jsonSibling + ".obsolete", overwrite: true); Log.Warn($"renamed legacy {jsonSibling} -> {jsonSibling}.obsolete"); }
            catch { }
        }
        bool existed = File.Exists(path);
        var cfg = TomlConfig.LoadOrCreate<ProxyConfig>(path);
        if (!existed) Log.Warn($"no config at {path}, wrote defaults");
        return cfg;
    }
}
