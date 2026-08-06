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
            catch { /* a read-only install directory keeps the stale file, which is inert:
                       LoadOrCreate only reads the .json sibling when the .toml is missing, and
                       the next line writes one */ }
        }
        bool existed = File.Exists(path);
        bool minted = !existed && !File.Exists(jsonSibling) && WriteFirstRunConfig(path);
        var cfg = TomlConfig.LoadOrCreate<ProxyConfig>(path);
        if (!existed)
        {
            Log.Warn($"no config at {path}, wrote defaults");
            if (minted)
                Log.Warn("registry.embedded_shared_secret was generated for this install; copy it into \"SharedSecret\" in each backend's nimbus-server.json");
        }
        return cfg;
    }

    // The file an operator has never seen, written before it is read back and validated. Two
    // things separate it from a plain serialization of the defaults:
    //
    //   - the registry shared secret is minted here rather than shipped as a literal, so two
    //     installs never share one and nothing published in a doc opens either (#40),
    //   - the value is useless to the operator without knowing where its twin goes, so the line
    //     above it says so.
    //
    // Skipped when a legacy nimbus.proxy.json is still sitting there, because that path is a
    // migration of an existing network whose secret the backends already have.
    //
    // Returns whether a secret was minted, so the caller only tells the operator to go copy one
    // when there is one to copy.
    internal static bool WriteFirstRunConfig(string path)
    {
        try
        {
            var fresh = new ProxyConfig();
            fresh.Registry.EmbeddedSharedSecret = SecretGenerator.NewSharedSecret();
            TomlConfig.Save(path, fresh);
            AnnotateSharedSecret(path);
            return true;
        }
        catch (Exception ex)
        {
            // LoadOrCreate writes the plain defaults right after this, and a config carrying the
            // known placeholder is one the validator refuses to serve on a public bind. Losing the
            // generated secret is worth a line in the log, not a failed start.
            Log.Warn("could not write a generated registry secret into the new config: " + ex.Message);
            return false;
        }
    }

    // Tomlyn serializes a POCO, so the only way a comment reaches the file is to put it there
    // afterwards. Best effort by design: a missing key leaves the file exactly as written, since a
    // valid config without a comment beats a mangled one.
    private static void AnnotateSharedSecret(string path)
    {
        const string key = "embedded_shared_secret = ";
        var lines = File.ReadAllLines(path).ToList();
        int at = lines.FindIndex(l => l.StartsWith(key, StringComparison.Ordinal));
        if (at < 0) return;
        lines.InsertRange(at, new[]
        {
            "# Generated for this install. Every backend authenticates to the registry with it:",
            "# copy this exact value into \"SharedSecret\" in each backend's nimbus-server.json.",
            "# Changing it here means changing it on every backend in the same pass.",
        });
        File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(false));
    }
}
