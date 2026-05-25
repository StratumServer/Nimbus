using System.Text.Json;

namespace Nimbus.Proxy;

internal static class Program
{
    private const string ConfigFileName = "nimbus.proxy.json";

    private static async Task<int> Main(string[] args)
    {
        Log.Info("Nimbus.Proxy starting");

        ProxyConfig cfg;
        try { cfg = LoadConfig(); }
        catch (Exception ex) { Log.Error("config load failed: " + ex.Message); return 2; }

        Log.TraceEnabled = cfg.VerboseLogging;
        Log.Info($"config: listen {cfg.ListenHost}:{cfg.ListenPort}  backend {cfg.DefaultBackend}  logBytes={cfg.LogTrafficBytes}  verbose={cfg.VerboseLogging}");

        RegistryClient? registry = null;
        if (cfg.Nimbus.Enabled)
        {
            if (string.IsNullOrWhiteSpace(cfg.Nimbus.RegistryUrl) || string.IsNullOrWhiteSpace(cfg.Nimbus.SharedSecret))
            {
                Log.Warn("Nimbus.Enabled=true but RegistryUrl/SharedSecret is unset, disabling registry integration");
            }
            else
            {
                registry = new RegistryClient(cfg.Nimbus);
                Log.Info($"Nimbus registry enabled: url={cfg.Nimbus.RegistryUrl}  proxyServerId={cfg.Nimbus.ProxyServerId}  failOnError={cfg.Nimbus.FailOnRegistryError}");
            }
        }
        else
        {
            Log.Info("Nimbus registry integration disabled (set Nimbus.Enabled=true to enable)");
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Log.Info("ctrl+c received, shutting down"); cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        var listener = new ProxyListener(cfg, cts.Token, registry);
        var udp = new UdpRelay(cfg, cts.Token, listener.UdpOverrides);
        var admin = new AdminListener(cfg, listener, cts.Token);
        try
        {
            await Task.WhenAll(listener.RunAsync(), udp.RunAsync(), admin.RunAsync()).ConfigureAwait(false);
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
        if (!File.Exists(path))
        {
            Log.Warn($"no config at {path}, writing defaults");
            var fresh = new ProxyConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(fresh, new JsonSerializerOptions { WriteIndented = true }));
            return fresh;
        }
        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<ProxyConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (cfg == null) throw new InvalidOperationException("config deserialized to null");
        cfg.DefaultBackend ??= new BackendEndpoint();
        cfg.Nimbus ??= new NimbusConfig();
        if (string.IsNullOrWhiteSpace(cfg.ListenHost)) cfg.ListenHost = "0.0.0.0";
        if (cfg.BufferSize <= 0) cfg.BufferSize = 16 * 1024;
        if (cfg.ConnectTimeoutMs <= 0) cfg.ConnectTimeoutMs = 5000;
        return cfg;
    }
}
