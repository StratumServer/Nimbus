using Nimbus.Shared;

namespace Nimbus.Proxy;

internal static class Program
{
    private const string ConfigFileName = "nimbus.proxy.toml";

    private static async Task<int> Main(string[] args)
    {
        Log.Info("Nimbus.Proxy starting");

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
            Log.Info($"config: bind {cfg.Bind}  servers={cfg.Servers.Count}  try=[{string.Join(",", cfg.Try)}]  logBytes={cfg.Logging.LogTrafficBytes}  verbose={cfg.Logging.Verbose}");
            Log.Info($"transfers: default_mode={cfg.Transfers.DefaultMode}  allow_seamless={cfg.Transfers.AllowSeamless}  require_capability={cfg.Transfers.RequireSeamlessCapability}  fallback_to_redirect={cfg.Transfers.FallbackToRedirectWhenSeamlessUnavailable}  unsafe_splice={cfg.Transfers.EnableUnsafeSeamlessSplice}");
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
        using var runtime = new ProxyRuntime(cfg, cts.Token, registryHost.Client);

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

    private static ProxyConfig LoadConfig()
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
